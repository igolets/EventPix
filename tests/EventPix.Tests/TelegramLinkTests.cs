using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventPix.Tests;

public sealed class TelegramLinkTests : IDisposable
{
    private readonly StubGateway _gateway = new("eventpix_bot");
    private readonly TestApp _app;

    public TelegramLinkTests()
    {
        _app = new TestApp(s => s.AddSingleton<ITelegramBotGateway>(_gateway));
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Endpoint_Mints_Url_And_Stores_Only_The_Code_Hash()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/telegram-link?mode=Digest"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LinkDto>();
        Assert.StartsWith("https://t.me/eventpix_bot?start=", payload!.Url, StringComparison.Ordinal);

        var code = payload.Url.Substring(payload.Url.LastIndexOf('=') + 1);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var link = await db.TelegramLinkCodes.SingleAsync();
        Assert.Equal(TelegramUpdateWorker.CodeHash(code), link.CodeHash);
        Assert.NotEqual(code, link.CodeHash);
        Assert.Equal(NotifyMode.Digest, link.Mode);
        Assert.Null(link.ConsumedUtc);
        Assert.True(link.ExpiresUtc > DateTime.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task Endpoint_With_Unknown_Tab_Is_Not_Found()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/telegram-link?tab=" + Guid.NewGuid()));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_Without_Bot_Is_Not_Implemented()
    {
        _gateway.Username = null;
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var response = await client.GetAsync(U("/api/e/" + token + "/subscriptions/telegram-link"));
        Assert.Equal(System.Net.HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Bot_Start_Confirms_Subscription_With_The_Code_Scope_And_Consumes_It()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (eventId, memberId, code) = await MintAsync(owner, NotifyMode.Digest);

        await Worker().OnMessageAsync(777, "/start " + code, CancellationToken.None);

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

        var link = await db.TelegramLinkCodes.SingleAsync();
        Assert.NotNull(link.ConsumedUtc);

        // A code is single-use: the same /start again only earns the rejection text.
        await Worker().OnMessageAsync(777, "/start " + code, CancellationToken.None);
        Assert.Single(await db.Subscriptions.ToListAsync());
        Assert.Collection(_gateway.Sent,
            m => Assert.Contains("Done!", m.Text, StringComparison.Ordinal),
            m => Assert.Contains("not valid", m.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bot_Start_Second_Link_Repoints_The_Same_Subscription()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (eventId, memberId, _) = await MintAsync(owner, NotifyMode.Instant);
        var first = await MintCodeAsync(eventId, memberId, NotifyMode.Instant);
        var second = await MintCodeAsync(eventId, memberId, NotifyMode.Instant);

        await Worker().OnMessageAsync(777, "/start " + first, CancellationToken.None);
        await Worker().OnMessageAsync(888, "/start " + second, CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal("888", sub.Address);
    }

    [Fact]
    public async Task Bot_Start_With_Unknown_Code_Is_Rejected_Without_State_Changes()
    {
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        await _app.CreateEventAsync(owner);

        await Worker().OnMessageAsync(42, "/start bogus", CancellationToken.None);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.TelegramLinkCodes.ToListAsync());
        Assert.Empty(await db.Subscriptions.ToListAsync());
        var message = Assert.Single(_gateway.Sent);
        Assert.Equal(42, message.ChatId);
        Assert.Contains("not valid", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_Start_Text_Is_Ignored()
    {
        await Worker().OnMessageAsync(42, "hello there", CancellationToken.None);
        await Worker().OnMessageAsync(42, null, CancellationToken.None);
        Assert.Empty(_gateway.Sent);
    }

    /// <summary>Creates an event, its owner and one fresh link code; returns their ids plus the raw code.</summary>
    private async Task<(Guid EventId, Guid MemberId, string Code)> MintAsync(string ownerEmail, NotifyMode mode)
    {
        var (_, eventId) = await _app.CreateEventAsync(ownerEmail);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId);
        var code = await MintCodeAsync(eventId, owner.Id, mode);
        return (eventId, owner.Id, code);
    }

    private async Task<string> MintCodeAsync(Guid eventId, Guid memberId, NotifyMode mode)
    {
        var now = DateTime.UtcNow;
        var code = Guid.NewGuid().ToString("N");
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        db.TelegramLinkCodes.Add(new TelegramLinkCode
        {
            CodeHash = TelegramUpdateWorker.CodeHash(code),
            EventId = eventId,
            MemberId = memberId,
            TabId = null,
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

    private TelegramUpdateWorker Worker()
    {
        return _app.Factory.Services.GetServices<IHostedService>().OfType<TelegramUpdateWorker>().Single();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record LinkDto(string Url);

    private sealed class StubGateway : ITelegramBotGateway
    {
        public StubGateway(string? username)
        {
            Username = username;
        }

        public string? Username { get; set; }

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

        public Task ReceiveAsync(Func<long, string?, CancellationToken, Task> onMessage, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
