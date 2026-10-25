using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Features;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

/// <summary>M1 integration tests: owner tab management, invite list, one-click unsubscribe.</summary>
public sealed class OwnerEndpointsTests : IDisposable
{
    // The API serialises enums as strings (JsonStringEnumConverter); mirror that here.
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
    public async Task Owner_Can_Rename_Reorder_Delete_Tab_And_Blobs()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var a = await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "A", AllowGuestUploads = false });
        var b = await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "B", AllowGuestUploads = false });
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        var idA = (await a.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var idB = (await b.Content.ReadFromJsonAsync<IdDto>())!.Id;

        // Rename + open the tab for guest uploads.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{idA}"),
            new { Name = "A2", AllowGuestUploads = true })).StatusCode);
        var afterPatch = await client.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), _json);
        Assert.Contains(afterPatch!, t => t.Id == idA && t.Name == "A2" && t.CanUpload);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{idA}"),
            new { Name = "   " })).StatusCode);

        // Reorder: B before A2.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(U($"/api/e/{token}/tabs/order"),
            new { Ids = new[] { idB, idA } })).StatusCode);
        var afterOrder = await client.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), _json);
        Assert.Equal(new[] { idB, idA }, afterOrder!.Where(t => t.Kind == TabKind.Section).Select(t => t.Id).ToArray());

        // Unknown tab id -> 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{Guid.NewGuid()}"),
            new { Name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(U($"/api/e/{token}/tabs/order"),
            new { Ids = new[] { idA, Guid.NewGuid() } })).StatusCode);

        // Deleting a tab removes its media rows and the blobs.
        var key = await AddMediaAsync(eventId, owner, idA);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U($"/api/e/{token}/tabs/{idA}"))).StatusCode);
        var afterDelete = await client.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), _json);
        Assert.DoesNotContain(afterDelete!, t => t.Id == idA);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(U($"/api/e/{token}/tabs/{idA}/media"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(U($"/api/e/{token}/tabs/{idA}"))).StatusCode);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        Assert.False(storage.Exists(key + "/original"));
        Assert.False(storage.Exists(key + "/thumb.jpg"));
        Assert.False(storage.Exists(key + "/preview.jpg"));
    }

    [Fact]
    public async Task Guest_Cannot_Manage_Tabs_Or_Invites()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var guest = $"guest-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var ownerClient = _app.CreateClient();
        await _app.SignInAsync(ownerClient, token, owner);
        var created = await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "A", AllowGuestUploads = false });
        var tabId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var guestClient = _app.CreateClient();
        await _app.SignInAsync(guestClient, token, guest);

        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{tabId}"), new { Name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.PutAsJsonAsync(U($"/api/e/{token}/tabs/order"), new { Ids = new[] { tabId } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.DeleteAsync(U($"/api/e/{token}/tabs/{tabId}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.GetAsync(U($"/api/e/{token}/invites"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.PostAsJsonAsync(U($"/api/e/{token}/invites"), new { Email = guest })).StatusCode);
    }

    [Fact]
    public async Task InviteOnly_Gates_Signin_And_Manages_List()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var stranger = $"stranger-{Guid.NewGuid():N}@example.com";
        var invited = $"invited-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner, inviteOnly: true);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(U($"/api/e/{token}/invites"), new { Email = invited })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(U($"/api/e/{token}/invites"), new { Email = invited })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(U($"/api/e/{token}/invites"), new { Email = "not-an-email" })).StatusCode);

        var list = await client.GetFromJsonAsync<InviteDto[]>(U($"/api/e/{token}/invites"), _json);
        var entry = Assert.Single(list!, i => i.Email == invited);
        Assert.False(entry.Joined);

        // A stranger gets 202 but silently receives no code (no enumeration).
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync(U($"/api/e/{token}/auth/request"), new { Email = stranger })).StatusCode);
        Assert.False(await HasLoginCodeAsync(eventId, stranger));

        // The invited address does get a code and can join.
        var guestClient = _app.CreateClient();
        await _app.SignInAsync(guestClient, token, invited);
        Assert.True(await HasLoginCodeAsync(eventId, invited));
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync(U($"/api/e/{token}/me"))).StatusCode);

        // Removing the invite does not touch existing members.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U($"/api/e/{token}/invites/{entry.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(U($"/api/e/{token}/invites/{entry.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync(U($"/api/e/{token}/me"))).StatusCode);
    }

    [Fact]
    public async Task Unsubscribe_Link_Removes_Subscription_And_Page_Shell_Serves()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var guest = $"guest-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);

        Guid subId;
        string sig;
        string otherSig;
        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<EventPixDb>();
            var pepper = sp.GetRequiredService<SecretPepper>();
            var now = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
            var member = new Member { EventId = eventId, Email = guest, DisplayName = "guest", Role = MemberRole.Guest, CreatedUtc = now };
            db.Members.Add(member);
            var sub = new Subscription
            {
                EventId = eventId,
                MemberId = member.Id,
                Channel = NotifyChannel.Email,
                Address = guest,
                TabId = null,
                Mode = NotifyMode.Instant,
                Confirmed = true,
                CreatedUtc = now,
            };
            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
            sig = pepper.Hmac("unsub|" + subId.ToString("N"));
            otherSig = pepper.Hmac("other");
        }

        // The page shell renders without auth; the JS then POSTs to the API.
        var page = await _app.CreateClient().GetAsync(U($"/u/{subId:N}.{sig}"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);

        var client = _app.CreateClient();   // no session needed: the HMAC is the credential
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(U($"/api/unsubscribe/{subId}/{sig}"), null)).StatusCode);
        // Valid signature stays 204 even when already unsubscribed (no state revelation); bad signature is 404.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(U($"/api/unsubscribe/{subId}/{sig}"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(U($"/api/unsubscribe/{subId}/{otherSig}"), null)).StatusCode);

        using var check = _app.Factory.Services.CreateAsyncScope();
        var db2 = check.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.False(await db2.Subscriptions.AnyAsync(s => s.Id == subId));
    }

    [Fact]
    public async Task Owner_Endpoint_Edge_Cases_Are_Rejected()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"),
            new { Name = new string('x', 101), AllowGuestUploads = false })).StatusCode);

        var created = await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "A", AllowGuestUploads = false });
        var tabId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(U($"/api/e/{token}/tabs/order"),
            new { Ids = new[] { tabId, tabId } })).StatusCode);

        // Author tabs are renameable now; an empty patch is still fine.
        var authorTabId = await AddAuthorTabAsync(eventId, owner);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{authorTabId}"),
            new { Name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{authorTabId}"),
            new { })).StatusCode);
    }

    [Fact]
    public async Task Owner_Creates_Personal_Tabs_And_Others_Cannot_Use_Them()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var guest = $"guest-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var ownerClient = _app.CreateClient();
        await _app.SignInAsync(ownerClient, token, owner);

        // Two named personal tabs.
        var beach = await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "Beach", Personal = true });
        var hike = await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "Hike", Personal = true });
        Assert.Equal(HttpStatusCode.Created, beach.StatusCode);
        Assert.Equal(HttpStatusCode.Created, hike.StatusCode);
        var idBeach = (await beach.Content.ReadFromJsonAsync<IdDto>())!.Id;
        var idHike = (await hike.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var tabs = await ownerClient.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), _json);
        Assert.Contains(tabs!, t => t.Id == idBeach && t.Kind == TabKind.Author && t.Mine && t.CanRename);
        Assert.Contains(tabs!, t => t.Id == idHike && t.Kind == TabKind.Author && t.Mine && t.CanRename);

        // Rename works; a name collision with another own tab is refused.
        Assert.Equal(HttpStatusCode.NoContent,
            (await ownerClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{idBeach}"), new { Name = "Sunset" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await ownerClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{idHike}"), new { Name = "Sunset" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await ownerClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "Sunset", Personal = true })).StatusCode);

        // The owner uploads into a selected personal tab.
        var upload = await UploadAsync(ownerClient, token, idBeach, $"beach-{Guid.NewGuid():N}.jpg");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var batch = await upload.Content.ReadFromJsonAsync<OpsTests.UploadBatch>();
        Assert.Equal(idBeach, batch!.TabId);

        // Guests can neither upload into nor manage the owner's personal tabs.
        var guestClient = _app.CreateClient();
        await _app.SignInAsync(guestClient, token, guest);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(guestClient, token, idBeach, "x.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await guestClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{idBeach}"), new { Name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guestClient.DeleteAsync(U($"/api/e/{token}/tabs/{idBeach}"))).StatusCode);

        // The owner deletes their own personal tab.
        Assert.Equal(HttpStatusCode.NoContent, (await ownerClient.DeleteAsync(U($"/api/e/{token}/tabs/{idHike}"))).StatusCode);
    }

    [Fact]
    public async Task Guest_Names_The_Default_Tab_And_Creates_Own_Tabs()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var guest = $"guest-{Guid.NewGuid():N}@example.com";
        var other = $"other-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);

        var guestClient = _app.CreateClient();
        await _app.SignInAsync(guestClient, token, guest);

        // A first upload creates the default author tab named after the display name...
        var upload = await UploadAsync(guestClient, token, tabId: null, $"first-{Guid.NewGuid():N}.jpg");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var batch = await upload.Content.ReadFromJsonAsync<OpsTests.UploadBatch>();
        var defaultTabId = batch!.TabId;

        // ...and the guest renames it away from the e-mail-derived name.
        Assert.Equal(HttpStatusCode.NoContent,
            (await guestClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{defaultTabId}"), new { Name = "My shots" })).StatusCode);

        // Additional named personal tab.
        var created = await guestClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "Second day", Personal = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var secondId = (await created.Content.ReadFromJsonAsync<IdDto>())!.Id;

        // Uploads into a selected personal tab land there.
        var upload2 = await UploadAsync(guestClient, token, secondId, $"second-{Guid.NewGuid():N}.jpg");
        Assert.Equal(HttpStatusCode.OK, upload2.StatusCode);
        var batch2 = await upload2.Content.ReadFromJsonAsync<OpsTests.UploadBatch>();
        Assert.Equal(secondId, batch2!.TabId);

        // Other members cannot upload into, rename or delete the guest's tabs.
        var otherClient = _app.CreateClient();
        await _app.SignInAsync(otherClient, token, other);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(otherClient, token, defaultTabId, "x.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherClient.PatchAsJsonAsync(U($"/api/e/{token}/tabs/{secondId}"), new { Name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherClient.DeleteAsync(U($"/api/e/{token}/tabs/{secondId}"))).StatusCode);

        // The same tab name is fine for a different member: uniqueness is per (member, name).
        Assert.Equal(HttpStatusCode.Created,
            (await otherClient.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "My shots", Personal = true })).StatusCode);
    }

    private static int _seq;

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, Guid? tabId, string name)
    {
        using var form = new MultipartFormDataContent();
        var jpeg = TestJpeg.Asymmetric(taken: $"2026:05:17 14:30:{Interlocked.Increment(ref _seq) % 60:00}");
        using var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(content, "files", name);
        var path = tabId is null ? $"/api/e/{token}/uploads" : $"/api/e/{token}/uploads?tab={tabId}";
        return await client.PostAsync(U(path), form);
    }

    [Fact]
    public async Task Request_Code_Is_Limited_Per_Window()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted,
                (await client.PostAsJsonAsync(U($"/api/e/{token}/auth/request"), new { Email = owner })).StatusCode);
        }

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Equal(3, await db.LoginCodes.CountAsync(c => c.EventId == eventId && c.Email == owner));
    }

    private async Task<Guid> AddAuthorTabAsync(Guid eventId, string ownerEmail)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId && m.Email == ownerEmail);
        var tab = new Tab { EventId = eventId, Name = owner.DisplayName, Kind = TabKind.Author, OwnerMemberId = owner.Id, SortOrder = 1000 };
        db.Tabs.Add(tab);
        await db.SaveChangesAsync();
        return tab.Id;
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    private async Task<string> AddMediaAsync(Guid eventId, string ownerEmail, Guid tabId)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var storage = sp.GetRequiredService<IMediaStorage>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId && m.Email == ownerEmail);

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
            TabId = tabId,
            UploaderId = owner.Id,
            Kind = MediaKind.Photo,
            Status = MediaStatus.Ready,
            OriginalFileName = "a.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 3,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])),
            StorageKey = key,
            UploadedUtc = owner.CreatedUtc,
        });
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<bool> HasLoginCodeAsync(Guid eventId, string email)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        return await db.LoginCodes.AnyAsync(c => c.EventId == eventId && c.Email == email);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record IdDto(Guid Id);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record InviteDto(Guid Id, string Email, DateTime CreatedUtc, bool Joined);
}
