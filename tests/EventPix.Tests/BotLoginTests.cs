using System.Net;
using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Bot sign-in: the browser mints a single-use request, the worker proves the chat's messenger
/// identity on /start, and the polling browser exchanges the confirmation for the session cookie.
/// Members carry the messenger channel + id so uploads and stars attribute to a stable identity;
/// e-mail sign-ins remain a separate member by design.
/// </summary>
public sealed class BotLoginTests : IDisposable
{
    private readonly StubBotGateway _telegram = new(NotifyChannel.Telegram, "eventpix_bot");
    private readonly StubBotGateway _max = new(NotifyChannel.Max, "eventpix_max");
    private readonly TestApp _app;

    public BotLoginTests()
    {
        _app = new TestApp(s =>
        {
            s.AddSingleton<IBotLinkGateway>(_telegram);
            s.AddSingleton<IBotLinkGateway>(_max);
        });
    }

    public void Dispose()
    {
        _app.Dispose();
    }

    [Fact]
    public async Task Request_Mints_A_Deep_Link_And_Stores_Only_The_Code_Hash()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/request"), new { Channel = "Telegram" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LinkDto>();
        Assert.StartsWith("https://t.me/eventpix_bot?start=", payload!.Url, StringComparison.Ordinal);
        Assert.True(payload.ExpiresUtc > DateTime.UtcNow.AddMinutes(14));

        var code = payload.Url.Substring(payload.Url.LastIndexOf('=') + 1);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var request = await db.LoginRequests.SingleAsync();
        Assert.Equal(eventId, request.EventId);
        Assert.Equal(NotifyChannel.Telegram, request.Channel);
        Assert.Equal(BotLinkWorker.CodeHash(code), request.CodeHash);
        Assert.NotEqual(code, request.CodeHash);   // the raw code never touches the database
        Assert.Null(request.MemberId);
        Assert.Null(request.ConsumedUtc);
    }

    [Fact]
    public async Task Request_Supports_Max_And_Rejects_Non_Bot_Channels()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var max = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/request"), new { Channel = "Max" });
        Assert.Equal(HttpStatusCode.OK, max.StatusCode);
        var payload = await max.Content.ReadFromJsonAsync<LinkDto>();
        Assert.StartsWith("https://max.ru/eventpix_max?start=", payload!.Url, StringComparison.Ordinal);

        var email = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/request"), new { Channel = "Email" });
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode);
    }

    [Fact]
    public async Task Request_Without_A_Configured_Bot_Is_Not_Implemented()
    {
        _telegram.Username = null;
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/request"), new { Channel = "Telegram" });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Poll_Is_Uniform_For_Unknown_And_Unconfirmed_Codes()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var unknown = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = "not-a-code" });
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal("pending", (await unknown.Content.ReadFromJsonAsync<PollDto>())!.Status);

        var code = await MintViaApiAsync(client, token);
        var unconfirmed = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });
        Assert.Equal(HttpStatusCode.OK, unconfirmed.StatusCode);
        Assert.Equal("pending", (await unconfirmed.Content.ReadFromJsonAsync<PollDto>())!.Status);
    }

    [Fact]
    public async Task Start_On_An_Open_Event_Creates_A_Guest_Member_And_The_Poll_Signs_In()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var code = await MintViaApiAsync(client, token);
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + code, "Durov", "durov"), CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var member = await db.Members.SingleAsync(m => m.MessengerId != null);
        Assert.Null(member.Email);
        Assert.Equal(NotifyChannel.Telegram, member.MessengerChannel);
        Assert.Equal("777", member.MessengerId);
        Assert.Equal("durov", member.MessengerHandle);
        Assert.Equal("Durov", member.DisplayName);
        Assert.Equal(MemberRole.Guest, member.Role);
        Assert.NotNull((await db.LoginRequests.SingleAsync()).MemberId);

        // The polling browser consumes the confirmation and receives the session.
        var poll = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });
        Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);

        var me = await client.GetAsync(U($"/api/e/{token}/me"));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Exactly once: a second poller with the same code stays pending.
        var again = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });
        Assert.Equal("pending", (await again.Content.ReadFromJsonAsync<PollDto>())!.Status);

        // The chat hears what happened.
        var message = Assert.Single(_telegram.Sent);
        Assert.Equal(777, message.ChatId);
        Assert.Contains("Signed in", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Returning_Messenger_Member_Is_Not_Duplicated()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var first = await MintViaApiAsync(client, token);
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + first, "Durov", "durov"), CancellationToken.None);

        var second = await MintViaApiAsync(client, token);
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + second, "Durov", "durov"), CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Single(await db.Members.Where(m => m.MessengerId != null).ToListAsync());
    }

    [Fact]
    public async Task InviteOnly_Without_An_Invite_Is_Refused_Without_Consuming_The_Request()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner, inviteOnly: true);
        var client = _app.CreateClient();

        var code = await MintViaApiAsync(client, token, "Max");
        await Worker().OnUpdateAsync(_max, Msg(555, "/start " + code, "Stranger", "stranger"), CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.Members.Where(m => m.MessengerId != null).ToListAsync());
        Assert.Null((await db.LoginRequests.SingleAsync()).MemberId);   // the real invitee can still use it
        Assert.Contains("not on the list", Assert.Single(_max.Sent).Text, StringComparison.Ordinal);

        var poll = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });
        Assert.Equal("pending", (await poll.Content.ReadFromJsonAsync<PollDto>())!.Status);
    }

    [Fact]
    public async Task InviteOnly_With_A_Handle_Invite_Joins_On_First_Start()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner, inviteOnly: true);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var added = await client.PostAsJsonAsync(U($"/api/e/{token}/invites"), new { Channel = "Telegram", Handle = "@Durov" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var code = await MintViaApiAsync(client, token);
        // Handles are matched case-insensitively and without the @ prefix.
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + code, "Durov", "DUROV"), CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var member = await db.Members.SingleAsync(m => m.MessengerId != null);
        Assert.Equal("777", member.MessengerId);
        Assert.Equal("durov", member.MessengerHandle);
        var poll = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });
        Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
    }

    [Fact]
    public async Task A_Code_Is_Only_Confirmed_By_Its_Own_Channel()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var code = await MintViaApiAsync(client, token);
        await Worker().OnUpdateAsync(_max, Msg(555, "/start " + code, "Stranger", "stranger"), CancellationToken.None);

        Assert.DoesNotContain(_max.Sent, m => m.Text.Contains("Signed in", StringComparison.Ordinal));
        Assert.Empty(await MembersAsync());
        Assert.DoesNotContain(_telegram.Sent, _ => true);

        // The right bot still accepts the untouched request.
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + code, "Durov", "durov"), CancellationToken.None);
        Assert.Single(await MembersAsync());
    }

    [Fact]
    public async Task An_Expired_Request_Is_Rejected_By_The_Worker()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        await _app.CreateEventAsync(owner);
        var now = DateTime.UtcNow;
        var code = "expired-code";
        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            db.LoginRequests.Add(new LoginRequest
            {
                CodeHash = BotLinkWorker.CodeHash(code),
                Channel = NotifyChannel.Telegram,
                EventId = (await db.Events.SingleAsync()).Id,
                CreatedUtc = now.AddMinutes(-20),
                ExpiresUtc = now.AddMinutes(-5),
            });
            await db.SaveChangesAsync();
        }

        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + code), CancellationToken.None);
        Assert.Empty(await MembersAsync());
        Assert.Contains("not valid", Assert.Single(_telegram.Sent).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_Requires_The_Csrf_Header_And_Rate_Limits()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);

        // No X-Requested-With: the CSRF middleware rejects the state-changing call.
        var bare = _app.Factory.CreateClient();
        var csrf = await bare.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);

        // The dedicated poll policy is generous for the 3-second loop but still bounded.
        var client = _app.CreateClient();
        HttpStatusCode? last = null;
        for (var i = 0; i < 125; i++)
        {
            last = (await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = "x" })).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task A_Bot_Member_Cannot_Subscribe_The_Email_Channel()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();

        var code = await MintViaApiAsync(client, token);
        await Worker().OnUpdateAsync(_telegram, Msg(777, "/start " + code, "Durov", "durov"), CancellationToken.None);
        await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/poll"), new { Code = code });

        var put = await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"), new { Channel = "Email", Mode = "Instant" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    private async Task<List<Member>> MembersAsync()
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        return await db.Members.Where(m => m.MessengerId != null).ToListAsync();
    }

    /// <summary>Mints a login request through the API, as the browser would.</summary>
    private static async Task<string> MintViaApiAsync(HttpClient client, string token, string channel = "Telegram")
    {
        var response = await client.PostAsJsonAsync(U($"/api/e/{token}/auth/bot/request"), new { Channel = channel });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LinkDto>();
        return payload!.Url.Substring(payload.Url.LastIndexOf('=') + 1);
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    private BotLinkWorker Worker()
    {
        return _app.Factory.Services.GetServices<IHostedService>().OfType<BotLinkWorker>().Single();
    }

    private static BotMessage Msg(long chatId, string? text, string? name = null, string? username = null)
    {
        return new BotMessage(chatId, chatId, name, username, text);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record LinkDto(string Url, DateTime ExpiresUtc);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record PollDto(string Status);

    /// <summary>Same seam stub as BotLinkTests: the worker is driven directly, ReceiveAsync never fires.</summary>
    private sealed class StubBotGateway(NotifyChannel channel, string? username) : IBotLinkGateway
    {
        public NotifyChannel Channel { get; } = channel;

        public string? Username { get; set; } = username;

        public List<(long ChatId, string Text)> Sent { get; } = [];

        public Task<string?> GetBotUsernameAsync(CancellationToken ct)
        {
            return Task.FromResult(Username);
        }

        public Task SendAsync(long chatId, string text, CancellationToken ct)
        {
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public string BuildLinkUrl(string botName, string code)
        {
            return Channel == NotifyChannel.Max
                ? $"https://max.ru/{botName}?start={code}"
                : $"https://t.me/{botName}?start={code}";
        }

        public Task ReceiveAsync(Func<BotMessage, CancellationToken, Task> onMessage, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
