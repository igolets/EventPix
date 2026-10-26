using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Guest viewing: anonymous visitors of open events browse read-only; anything that mutates state,
/// starred downloads, and every invite-only album still needs an OTP session.
/// </summary>
public sealed class GuestViewingTests : IDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TestApp _app = new();

    public void Dispose()
    {
        _app.Dispose();
    }

    [Fact]
    public async Task Guest_Browses_Open_Event_Read_Only()
    {
        var (token, tabId, mediaId) = await SeedAsync();
        var guest = _app.CreateClient();   // never signed in

        var tabs = await guest.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), _json);
        var tab = Assert.Single(tabs!);
        Assert.False(tab.CanUpload);
        Assert.False(tab.Mine);
        Assert.False(tab.CanRename);

        var media = await guest.GetFromJsonAsync<GuestMediaDto[]>(U($"/api/e/{token}/tabs/{tabId}/media"), _json);
        var item = Assert.Single(media!);
        Assert.False(item.Starred);
        Assert.False(item.CanDelete);

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(U($"/api/e/{token}/media/{mediaId}/thumb"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(U($"/api/e/{token}/media/{mediaId}/preview"))).StatusCode);
    }

    [Fact]
    public async Task Guest_Cannot_Mutate_Or_Read_Private_Settings()
    {
        var (token, tabId, mediaId) = await SeedAsync();
        var guest = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/me"))).StatusCode);

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(TestJpeg.Asymmetric());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "files", "x.jpg");
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsync(U($"/api/e/{token}/uploads"), form)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PutAsync(U($"/api/e/{token}/media/{mediaId}/star"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.DeleteAsync(U($"/api/e/{token}/media/{mediaId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{tabId}"), new { Name = "Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PutAsJsonAsync(U($"/api/e/{token}/tabs/order"), new { Ids = new[] { tabId } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/subscriptions"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/invites"))).StatusCode);
    }

    [Fact]
    public async Task Guest_Downloads_Follow_The_Event_Flag()
    {
        var (token, _, _) = await SeedAsync();
        var guest = _app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(U($"/api/e/{token}/download"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/download?starred=true"))).StatusCode);

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            await db.Events.ExecuteUpdateAsync(s => s.SetProperty(e => e.DownloadsOpen, false));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync(U($"/api/e/{token}/download"))).StatusCode);
    }

    [Fact]
    public async Task InviteOnly_Events_Keep_The_Login_Wall_For_Viewing()
    {
        var (token, tabId, mediaId) = await SeedAsync(inviteOnly: true);
        var guest = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/tabs"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/tabs/{tabId}/media"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync(U($"/api/e/{token}/media/{mediaId}/thumb"))).StatusCode);
    }

    /// <summary>Owner, one section tab, one Ready photo. Returns the secret token and the ids.</summary>
    private async Task<(string Token, Guid TabId, Guid MediaId)> SeedAsync(bool inviteOnly = false)
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner, inviteOnly);
        var ownerClient = _app.CreateClient();
        await _app.SignInAsync(ownerClient, token, owner);
        var created = await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "A", AllowGuestUploads = false });
        var tabId = (await created.Content.ReadFromJsonAsync<OwnerEndpointsTests.IdDto>())!.Id;

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(TestJpeg.Asymmetric());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "files", "photo.jpg");
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsync(U($"/api/e/{token}/uploads?tab={tabId}"), form)).StatusCode);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var mediaId = Guid.Empty;
        for (var i = 0; i < 100 && mediaId == Guid.Empty; i++)
        {
            var item = await db.MediaItems.AsNoTracking().SingleAsync(m => m.EventId == eventId);
            if (item.Status == MediaStatus.Ready)
            {
                mediaId = item.Id;
            }
            else
            {
                await Task.Delay(100);
            }
        }

        Assert.NotEqual(Guid.Empty, mediaId);
        return (token, tabId, mediaId);
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record GuestMediaDto(bool Starred, bool CanDelete);
}
