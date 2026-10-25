using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

/// <summary>M2 integration tests: multi-select download (POST /download ids) and media pagination.</summary>
public sealed class DownloadEndpointsTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Post_Download_Ids_Zips_Only_This_Event_Items()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        // A second event's item must never leak into this event's ZIP.
        var otherOwner = $"other-{Guid.NewGuid():N}@example.com";
        var (_, otherEventId) = await _app.CreateEventAsync(otherOwner);
        var (tabA, ids) = await AddItemsAsync(eventId, owner, 2);
        var otherTab = await NewTabAsync(otherEventId);
        var (_, foreignIds) = await AddItemsAsync(otherEventId, otherOwner, 1, otherTab);

        var response = await client.PostAsJsonAsync(U($"/api/e/{token}/download"),
            new { Ids = ids.Concat(foreignIds).Append(Guid.NewGuid()).ToArray() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("zip", response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.Equal(2, zip.Entries.Count);
        Assert.All(zip.Entries, e => Assert.EndsWith(".jpg", e.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_Download_Ids_Rejects_Empty_TooMany_And_Empty_Result()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(U($"/api/e/{token}/download"), new { Ids = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(U($"/api/e/{token}/download"), new { Ids = Enumerable.Repeat(Guid.NewGuid(), 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(U($"/api/e/{token}/download"), new { Ids = new[] { Guid.NewGuid() } })).StatusCode);
    }

    [Fact]
    public async Task Post_Download_Ids_Respects_DownloadsOpen_For_Guests()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var guest = $"guest-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            await db.Events.Where(e => e.Id == eventId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DownloadsOpen, false));
        }

        var ownerClient = _app.CreateClient();
        await _app.SignInAsync(ownerClient, token, owner);
        var tab = await NewTabAsync(eventId);
        var (_, ids) = await AddItemsAsync(eventId, owner, 1, tab);

        // Owner bypasses DownloadsOpen; guests do not.
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/download"), new { Ids = ids })).StatusCode);

        var guestClient = _app.CreateClient();
        await _app.SignInAsync(guestClient, token, guest);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await guestClient.PostAsJsonAsync(U($"/api/e/{token}/download"), new { Ids = ids })).StatusCode);
    }

    [Fact]
    public async Task Media_List_Paginates_In_Upload_Order()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);
        var tab = await NewTabAsync(eventId);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var added = await AddItemsAsync(eventId, owner, 1, tab, uploadedUtc: DateTime.UtcNow.AddMinutes(i));
            ids.AddRange(added.Ids);
        }

        var page1 = await client.GetFromJsonAsync<EventPix.Web.Features.MediaDto[]>(U($"/api/e/{token}/tabs/{tab}/media?skip=0&take=2"), _json);
        var page2 = await client.GetFromJsonAsync<EventPix.Web.Features.MediaDto[]>(U($"/api/e/{token}/tabs/{tab}/media?skip=2&take=2"), _json);

        Assert.Equal(2, page1!.Length);
        Assert.Single(page2!);
        Assert.Equal(new[] { ids[0], ids[1] }, page1.Select(m => m.Id).ToArray());
        Assert.Equal(ids[2], page2![0].Id);
    }

    private static readonly System.Text.Json.JsonSerializerOptions _json = new(
        System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    private async Task<Guid> NewTabAsync(Guid eventId)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var tab = new Tab { EventId = eventId, Name = $"Tab-{Guid.NewGuid():N}", Kind = TabKind.Section, SortOrder = 1 };
        db.Tabs.Add(tab);
        await db.SaveChangesAsync();
        return tab.Id;
    }

    private async Task<(Guid Tab, List<Guid> Ids)> AddItemsAsync(
        Guid eventId, string ownerEmail, int count, Guid? tabId = null, DateTime? uploadedUtc = null)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var storage = sp.GetRequiredService<IMediaStorage>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId && m.Email == ownerEmail);
        var tab = tabId ?? await NewTabAsync(eventId);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            var key = $"{eventId:N}/{id:N}";
            var bytes = new MemoryStream([1, 2, 3]);
            await storage.SaveAsync(key + "/original", bytes, CancellationToken.None);
            await storage.SaveAsync(key + "/thumb.jpg", bytes, CancellationToken.None);
            await storage.SaveAsync(key + "/preview.jpg", bytes, CancellationToken.None);
            db.MediaItems.Add(new MediaItem
            {
                Id = id,
                EventId = eventId,
                TabId = tab,
                UploaderId = owner.Id,
                Kind = MediaKind.Photo,
                Status = MediaStatus.Ready,
                OriginalFileName = $"p{ids.Count}.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 3,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])),
                StorageKey = key,
                UploadedUtc = uploadedUtc ?? owner.CreatedUtc,
            });
            ids.Add(id);
        }

        await db.SaveChangesAsync();
        return (tab, ids);
    }
}
