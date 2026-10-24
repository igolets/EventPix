using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

/// <summary>M1 integration test: upload -> media worker -> Ready, with EXIF orientation and capture time applied.</summary>
public sealed class UploadPipelineTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Upload_Photo_Becomes_Ready_With_Orientation_And_TakenUtc()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        using var form = new MultipartFormDataContent();
        var jpeg = TestJpeg.Asymmetric(orientation: 6, taken: "2026:05:17 14:30:00", offset: "+02:00");
        var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "files", "photo.jpg");

        var response = await client.PostAsync(U($"/api/e/{token}/uploads"), form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<UploadResult>();
        var uploaded = Assert.Single(payload!.Results);
        Assert.Equal("photo.jpg", uploaded.Name);
        Assert.Null(uploaded.Error);   // per-file failures come back as { name, error }

        // The background worker picks the item up asynchronously; poll until Ready.
        MediaItem? item = null;
        for (var i = 0; i < 100; i++)
        {
            item = await GetMediaAsync(uploaded.Id);
            if (item is { Status: MediaStatus.Ready })
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(item);
        Assert.Equal(MediaStatus.Ready, item!.Status);
        Assert.Equal(MediaKind.Photo, item.Kind);
        Assert.Equal(eventId, item.EventId);

        // EXIF orientation 6 = rotate 90 CW: variants are stored upright, 40x20 source -> 20x40 displayed.
        Assert.Equal(20, item.Width);
        Assert.Equal(40, item.Height);
        Assert.Equal(new DateTime(2026, 5, 17, 12, 30, 0), item.TakenUtc);   // +02:00 offset applied -> UTC

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        Assert.True(storage.Exists(item.StorageKey + "/original"));
        Assert.True(storage.Exists(item.StorageKey + "/thumb.jpg"));
        Assert.True(storage.Exists(item.StorageKey + "/preview.jpg"));

        // List, star, and download the tab. MediaDto carries string-serialised enums; mirror the API's options.
        var list = await client.GetFromJsonAsync<EventPix.Web.Features.MediaDto[]>(U($"/api/e/{token}/tabs/{payload.TabId}/media"), _json);
        var entry = Assert.Single(list!);
        Assert.Equal(uploaded.Id, entry.Id);
        Assert.False(entry.Starred);
        Assert.Equal(20, entry.Width);
        Assert.Equal(40, entry.Height);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync(U($"/api/e/{token}/media/{uploaded.Id}/star"), null)).StatusCode);
        var starred = await client.GetFromJsonAsync<EventPix.Web.Features.MediaDto[]>(U($"/api/e/{token}/tabs/{payload.TabId}/media"), _json);
        Assert.True(Assert.Single(starred!).Starred);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U($"/api/e/{token}/media/{uploaded.Id}/star"))).StatusCode);

        var zip = await client.GetAsync(U($"/api/e/{token}/download"));
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Contains("zip", zip.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);
        Assert.True((await zip.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    private static readonly System.Text.Json.JsonSerializerOptions _json = new(
        System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static Uri U(string path) => new(path, UriKind.Relative);

    private async Task<MediaItem?> GetMediaAsync(Guid id)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        return await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record UploadResult(Guid TabId, List<UploadedFile> Results);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record UploadedFile(string Name, Guid Id, string? Error);
}
