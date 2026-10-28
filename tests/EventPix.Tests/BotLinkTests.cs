using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventPix.Tests;

public sealed class BotLinkTests : IDisposable
{
    private readonly StubGateway _telegram = new(NotifyChannel.Telegram, "eventpix_bot");
    private readonly StubGateway _max = new(NotifyChannel.Max, "eventpix_max");
    private readonly TestApp _app;

    public BotLinkTests()
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
    public async Task Telegram_Link_Mints_Url_And_Stores_Only_The_Code_Hash()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link?channel=Telegram&mode=Digest"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LinkDto>();
        Assert.StartsWith("https://t.me/eventpix_bot?start=", payload!.Url, StringComparison.Ordinal);

        var code = payload.Url.Substring(payload.Url.LastIndexOf('=') + 1);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var link = await db.BotLinkCodes.SingleAsync();
        Assert.Equal(NotifyChannel.Telegram, link.Channel);
        Assert.Equal(BotLinkWorker.CodeHash(code), link.CodeHash);
        Assert.NotEqual(code, link.CodeHash);
        Assert.Equal(NotifyMode.Digest, link.Mode);
        Assert.Null(link.ConsumedUtc);
        Assert.True(link.ExpiresUtc > DateTime.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task Max_Link_Mints_A_Max_Deep_Link_For_The_Max_Channel()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link?channel=Max"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LinkDto>();
        Assert.StartsWith("https://max.ru/eventpix_max?start=", payload!.Url, StringComparison.Ordinal);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Equal(NotifyChannel.Max, (await db.BotLinkCodes.SingleAsync()).Channel);
    }

    [Fact]
    public async Task Bot_Link_Defaults_To_Telegram_And_Rejects_Non_Bot_Channels()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var telegram = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link"));
        Assert.Equal(System.Net.HttpStatusCode.OK, telegram.StatusCode);

        // A channel no bot gateway serves (e-mail is PUT, bots use links) is a bad request.
        var email = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link?channel=Email"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, email.StatusCode);
    }

    [Fact]
    public async Task Bot_Link_With_Unknown_Tab_Is_Not_Found()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link?tab=" + Guid.NewGuid()));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Bot_Link_Without_Bot_Is_Not_Implemented()
    {
        _telegram.Username = null;
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/bot-link"));
        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Bot_Start_Confirms_Subscription_With_The_Code_Scope_And_Consumes_It()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (eventId, memberId, code) = await MintAsync(owner, NotifyChannel.Telegram, NotifyMode.Digest);

        await Worker().OnUpdateAsync(_telegram, 777, "/start " + code, CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal(eventId, sub.EventId);
        Assert.Equal(memberId, sub.MemberId);
        Assert.Equal(NotifyChannel.Telegram, sub.Channel);
        Assert.Equal("777", sub.Address);
        Assert.Equal(NotifyMode.Digest, sub.Mode);
        Assert.True(sub.Confirmed);
        Assert.Null(sub.TabId);

        var link = await db.BotLinkCodes.SingleAsync();
        Assert.NotNull(link.ConsumedUtc);

        // A code is single-use: the same /start again only earns the rejection text.
        await Worker().OnUpdateAsync(_telegram, 777, "/start " + code, CancellationToken.None);
        Assert.Single(await db.Subscriptions.ToListAsync());

        // The confirmation names the scope and cadence so a wrong link is caught immediately.
        Assert.Collection(_telegram.Sent,
            m =>
            {
                Assert.Contains("Done!", m.Text, StringComparison.Ordinal);
                Assert.Contains("\u201cTest event\u201d (all albums) once a day as a digest.", m.Text, StringComparison.Ordinal);
            },
            m => Assert.Contains("not valid", m.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bot_Start_Confirmation_Names_The_Tab_And_Digest_Window()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (_, eventId) = await _app.CreateEventAsync(owner);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var ownerMember = await db.Members.FirstAsync(m => m.EventId == eventId);
        var tab = new Tab { EventId = eventId, Name = "Ceremony", Kind = TabKind.Section };
        db.Tabs.Add(tab);
        await db.SaveChangesAsync();

        var code = await MintCodeAsync(eventId, ownerMember.Id, NotifyChannel.Max, NotifyMode.Digest, tab.Id);
        await Worker().OnUpdateAsync(_max, 555, "/start " + code, CancellationToken.None);

        var message = Assert.Single(_max.Sent);
        Assert.Contains("album \u201cCeremony\u201d", message.Text, StringComparison.Ordinal);
        Assert.Contains("once a day as a digest", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Max_Start_Confirms_A_Max_Subscription()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (eventId, memberId, code) = await MintAsync(owner, NotifyChannel.Max, NotifyMode.Instant);

        await Worker().OnUpdateAsync(_max, 555, "/start " + code, CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal(eventId, sub.EventId);
        Assert.Equal(memberId, sub.MemberId);
        Assert.Equal(NotifyChannel.Max, sub.Channel);
        Assert.Equal("555", sub.Address);
        Assert.True(sub.Confirmed);
        Assert.Single(_max.Sent);
        Assert.Contains("\u201cTest event\u201d (all albums) as soon as it is added.", _max.Sent[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_telegram.Sent, _ => true);
    }

    [Fact]
    public async Task A_Code_Is_Only_Valid_For_Its_Own_Channel()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (_, _, code) = await MintAsync(owner, NotifyChannel.Max, NotifyMode.Instant);

        // Presenting a MAX code to the Telegram bot proves nothing and changes nothing...
        await Worker().OnUpdateAsync(_telegram, 777, "/start " + code, CancellationToken.None);
        Assert.Contains(_telegram.Sent, m => m.Text.Contains("not valid", StringComparison.Ordinal));
        Assert.DoesNotContain(_max.Sent, _ => true);

        // ...and the code itself stays unconsumed, so the right bot still accepts it.
        await Worker().OnUpdateAsync(_max, 555, "/start " + code, CancellationToken.None);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal(NotifyChannel.Max, sub.Channel);
        Assert.Equal("555", sub.Address);
    }

    [Fact]
    public async Task Bot_Start_Second_Link_Repoints_The_Same_Subscription()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (eventId, memberId, _) = await MintAsync(owner, NotifyChannel.Max, NotifyMode.Instant);
        var first = await MintCodeAsync(eventId, memberId, NotifyChannel.Max, NotifyMode.Instant);
        var second = await MintCodeAsync(eventId, memberId, NotifyChannel.Max, NotifyMode.Instant);

        await Worker().OnUpdateAsync(_max, 555, "/start " + first, CancellationToken.None);
        await Worker().OnUpdateAsync(_max, 666, "/start " + second, CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal("666", sub.Address);
    }

    [Fact]
    public async Task Bot_Start_With_Unknown_Code_Is_Rejected_Without_State_Changes()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        await _app.CreateEventAsync(owner);

        await Worker().OnUpdateAsync(_max, 42, "/start bogus", CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.BotLinkCodes.ToListAsync());
        Assert.Empty(await db.Subscriptions.ToListAsync());
        var message = Assert.Single(_max.Sent);
        Assert.Equal(42, message.ChatId);
        Assert.Contains("not valid", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_Start_Text_Is_Ignored()
    {
        await Worker().OnUpdateAsync(_telegram, 42, "hello there", CancellationToken.None);
        await Worker().OnUpdateAsync(_telegram, 42, null, CancellationToken.None);
        Assert.Empty(_telegram.Sent);
        Assert.Empty(_max.Sent);
    }

    [Fact]
    public void Max_Updates_Normalise_To_The_Start_Shape()
    {
        var payload = new MaxBotGateway.Update("bot_started", ChatId: 42, Payload: "abc", Message: null);
        Assert.Equal((42L, "/start abc"), MaxBotGateway.ToMessage(payload));

        // A typed /start in the dialog arrives as message_created.
        var text = new MaxBotGateway.Update("message_created", null, null,
            new MaxBotGateway.MaxMessage(new MaxBotGateway.Recipient(43), null, "/start def"));
        Assert.Equal((43L, "/start def"), MaxBotGateway.ToMessage(text));

        // Alternative message shape (text directly on the message) and unrelated updates.
        var flat = new MaxBotGateway.Update("message_created", 44, null,
            new MaxBotGateway.MaxMessage(null, new MaxBotGateway.MaxBody("/start ghi"), null));
        Assert.Equal((44L, "/start ghi"), MaxBotGateway.ToMessage(flat));
        Assert.Equal((0L, null), MaxBotGateway.ToMessage(new MaxBotGateway.Update("bot_stopped", 45, null, null)));
    }

    /// <summary>Creates an event, its owner and one fresh link code; returns their ids plus the raw code.</summary>
    private async Task<(Guid EventId, Guid MemberId, string Code)> MintAsync(
        string ownerEmail, NotifyChannel channel, NotifyMode mode)
    {
        var (_, eventId) = await _app.CreateEventAsync(ownerEmail);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId);
        var code = await MintCodeAsync(eventId, owner.Id, channel, mode);
        return (eventId, owner.Id, code);
    }

    private async Task<string> MintCodeAsync(
        Guid eventId, Guid memberId, NotifyChannel channel, NotifyMode mode, Guid? tabId = null)
    {
        var now = DateTime.UtcNow;
        var code = Guid.NewGuid().ToString("N");
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        db.BotLinkCodes.Add(new BotLinkCode
        {
            CodeHash = BotLinkWorker.CodeHash(code),
            Channel = channel,
            EventId = eventId,
            MemberId = memberId,
            TabId = tabId,
            Mode = mode,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(15),
        });
        await db.SaveChangesAsync();
        return code;
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    private BotLinkWorker Worker()
    {
        return _app.Factory.Services.GetServices<IHostedService>().OfType<BotLinkWorker>().Single();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record LinkDto(string Url);

    private sealed class StubGateway(NotifyChannel channel, string? username) : IBotLinkGateway
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

        public Task ReceiveAsync(Func<long, string?, CancellationToken, Task> onMessage, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
