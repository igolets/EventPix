using System.Security.Cryptography;
using System.Text;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EventPix.Web.Notifications;

/// <summary>
/// Thin seam over a chat bot (Telegram or MAX) holding everything EventPix needs:
/// the bot username for deep links, one-way messages, and /start long polling.
/// Keeps the worker and endpoints free of messenger types so tests can stub it.
/// </summary>
internal interface IBotLinkGateway
{
    public NotifyChannel Channel { get; }

    /// <summary>Bot username, or null when no token is configured (or the bot has none).</summary>
    public Task<string?> GetBotUsernameAsync(CancellationToken ct);

    public Task SendAsync(long chatId, string text, CancellationToken ct);

    /// <summary>Deep link the user opens to start the bot with a linking code.</summary>
    public string BuildLinkUrl(string botName, string code);

    /// <summary>Long-polls messages; returns immediately when no token is configured.</summary>
    public Task ReceiveAsync(Func<BotMessage, CancellationToken, Task> onMessage, CancellationToken ct);
}

/// <summary>
/// One inbound bot message, normalised across Telegram and MAX. SenderId is the messenger's stable
/// user id (in the private chats this flow uses, it equals ChatId) and is what a login binds to.
/// </summary>
internal sealed record BotMessage(
    long ChatId, long SenderId, string? SenderName, string? SenderUsername, string? Text);

internal sealed class TelegramBotGateway(IOptions<TelegramOptions> options) : IBotLinkGateway
{
    public NotifyChannel Channel => NotifyChannel.Telegram;

    public async Task<string?> GetBotUsernameAsync(CancellationToken ct)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var me = await new TelegramBotClient(token).GetMe(ct);
        return me.Username;
    }

    public async Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        await new TelegramBotClient(token).SendMessage(chatId, text, cancellationToken: ct);
    }

    public string BuildLinkUrl(string botName, string code)
    {
        return $"https://t.me/{botName}?start={code}";
    }

    public async Task ReceiveAsync(Func<BotMessage, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var receiver = new ReceiverOptions { AllowedUpdates = [UpdateType.Message] };
        await new TelegramBotClient(token).ReceiveAsync(
            (_, update, _) => onMessage(ToMessage(update.Message), ct),
            (_, exception, _) => Task.FromException(exception),
            receiver, ct);
    }

    /// <summary>Internal for tests: the Telegram update → BotMessage normalisation.</summary>
    internal static BotMessage ToMessage(Telegram.Bot.Types.Message? m)
    {
        if (m is null)
        {
            return new BotMessage(0, 0, null, null, null);
        }

        // From is the human sender (null only for anonymous channel posts, which this flow never uses);
        // fall back to the chat so SenderId is never 0 for a real private chat.
        var from = m.From;
        var name = string.Join(' ', new[] { from?.FirstName, from?.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new BotMessage(m.Chat.Id, from?.Id ?? m.Chat.Id,
            string.IsNullOrWhiteSpace(name) ? null : name, from?.Username, m.Text);
    }
}

/// <summary>
/// When a bot token is configured, long-polls every registered bot and turns "/start &lt;code&gt;" (Telegram)
/// or the deep-link payload (MAX, surfaced as the same text) into a confirmed subscription for the chat that
/// sent it. Codes come from GET /subscriptions/bot-link and are single-use, short-lived and stored only as
/// hashes. Without a token the gateway idles and so does this worker.
/// </summary>
internal sealed class BotLinkWorker(
    IServiceScopeFactory scopes, IEnumerable<IBotLinkGateway> gateways, TimeProvider clock,
    ILogger<BotLinkWorker> log) : BackgroundService
{
    private const string InvalidCodeText = "This linking code is not valid or has expired. " +
        "Open the album, tap Notify me and generate a new link.";

    private const string NotInvitedText = "This album is invite-only and this account is not on the list. " +
        "Ask the organiser to add your messenger handle, or sign in with your invited e-mail once.";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // One loop per channel. Last-wins per channel mirrors DI's single-service resolution,
        // so hosts (and tests) can override a bot by registering a later gateway.
        var tasks = gateways.GroupBy(g => g.Channel).Select(g => RunAsync(g.Last(), ct));
        await Task.WhenAll(tasks);
    }

    private async Task RunAsync(IBotLinkGateway gateway, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await gateway.ReceiveAsync((msg, c) => OnUpdateAsync(gateway, msg, c), ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.PollingFailed(e, gateway.Channel);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>Internal for tests: the message entry point the gateway drives.</summary>
    internal Task OnUpdateAsync(IBotLinkGateway gateway, BotMessage msg, CancellationToken ct)
    {
        return msg.Text is not null && msg.Text.StartsWith("/start", StringComparison.Ordinal)
            ? HandleStartAsync(gateway, msg, msg.Text[6..].Trim(), ct)
            : Task.CompletedTask;
    }

    private async Task HandleStartAsync(IBotLinkGateway gateway, BotMessage msg, string code, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var now = clock.GetUtcNow().UtcDateTime;

        // Codes are single-use and short-lived; drop the dead ones on every /start.
        await db.BotLinkCodes
            .Where(c => c.ExpiresUtc < now || c.ConsumedUtc != null)
            .ExecuteDeleteAsync(ct);
        await db.LoginRequests
            .Where(r => r.ExpiresUtc < now || r.ConsumedUtc != null)
            .ExecuteDeleteAsync(ct);

        var hash = CodeHash(code);
        var link = await db.BotLinkCodes.FirstOrDefaultAsync(
            c => c.CodeHash == hash && c.Channel == gateway.Channel &&
                 c.ConsumedUtc == null && c.ExpiresUtc > now, ct);
        if (link is not null)
        {
            await ConfirmSubscriptionAsync(db, gateway, msg.ChatId, link, now, ct);
            return;
        }

        await ConfirmLoginAsync(db, gateway, msg, hash, now, ct);
    }

    private static async Task ConfirmSubscriptionAsync(
        EventPixDb db, IBotLinkGateway gateway, long chatId, BotLinkCode link, DateTime now, CancellationToken ct)
    {
        link.ConsumedUtc = now;
        var ev = await db.Events.FirstAsync(e => e.Id == link.EventId, ct);
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s =>
            s.EventId == link.EventId && s.MemberId == link.MemberId &&
            s.Channel == gateway.Channel && s.TabId == link.TabId, ct);
        if (sub is null)
        {
            sub = new Subscription
            {
                EventId = link.EventId,
                MemberId = link.MemberId,
                Channel = gateway.Channel,
                Address = chatId.ToString(),
                TabId = link.TabId,
                Mode = link.Mode,
                Confirmed = true,   // the bot proves the chat id, like the sign-in code proves the e-mail
                CreatedUtc = now,
            };
            db.Subscriptions.Add(sub);
        }
        else
        {
            sub.Address = chatId.ToString();
            sub.Confirmed = true;
        }

        await db.SaveChangesAsync(ct);

        // The confirmation names the parameters so a wrong link is caught before anything happens.
        var scopeName = "all albums";
        if (link.TabId is not null)
        {
            var tab = await db.Tabs.FirstOrDefaultAsync(t => t.Id == link.TabId, ct);
            scopeName = tab is null ? "a removed album" : $"album \u201c{tab.Name}\u201d";
        }

        var cadence = link.Mode == NotifyMode.Digest ? "once a day as a digest" : "as soon as it is added";
        await gateway.SendAsync(chatId,
            $"Done! I will tell you about new media in \u201c{ev.Name}\u201d ({scopeName}) {cadence}.", ct);
    }

    /// <summary>
    /// Consumes a pending bot sign-in. The bot proves the sender's messenger identity: an existing
    /// member with that identity is reused; on open events anyone may join (like any e-mail could),
    /// on InviteOnly events the handle must be pre-registered. The request is only marked confirmed —
    /// the polling browser consumes it and gets the session.
    /// </summary>
    private static async Task ConfirmLoginAsync(
        EventPixDb db, IBotLinkGateway gateway, BotMessage msg, string hash, DateTime now, CancellationToken ct)
    {
        if (msg.SenderId <= 0)
        {
            return;   // no provable identity behind the message
        }

        var request = await db.LoginRequests.FirstOrDefaultAsync(
            r => r.CodeHash == hash && r.Channel == gateway.Channel &&
                 r.MemberId == null && r.ConsumedUtc == null && r.ExpiresUtc > now, ct);
        if (request is null)
        {
            await gateway.SendAsync(msg.ChatId, InvalidCodeText, ct);
            return;
        }

        var ev = await db.Events.FirstAsync(e => e.Id == request.EventId, ct);
        var messengerId = msg.SenderId.ToString();
        var handle = Tokens.NormalizeHandle(msg.SenderUsername);
        var member = await db.Members.FirstOrDefaultAsync(
            m => m.EventId == ev.Id && m.MessengerChannel == gateway.Channel && m.MessengerId == messengerId, ct);

        if (member is null)
        {
            if (ev.InviteOnly)
            {
                var invited = await db.Invites.AnyAsync(
                    i => i.EventId == ev.Id && i.MessengerChannel == gateway.Channel && i.MessengerHandle == handle, ct);
                if (!invited)
                {
                    await gateway.SendAsync(msg.ChatId, NotInvitedText, ct);
                    return;
                }
            }

            member = new Member
            {
                EventId = ev.Id,
                MessengerChannel = gateway.Channel,
                MessengerId = messengerId,
                MessengerHandle = handle,
                DisplayName = DisplayName(msg, handle),
                Role = MemberRole.Guest,   // the owner identity stays the e-mail; bot sign-ins are guests
                CreatedUtc = now,
            };
            db.Members.Add(member);
        }
        else
        {
            member.MessengerHandle ??= handle;   // usernames appear/disappear; keep the first one we saw
            member.LastSeenUtc = now;
        }

        request.MemberId = member.Id;
        await db.SaveChangesAsync(ct);
        await gateway.SendAsync(msg.ChatId,
            $"Signed in to \u201c{ev.Name}\u201d as {member.DisplayName}. " +
            "Return to the browser tab where you started; it will finish by itself.", ct);
    }

    private static string DisplayName(BotMessage msg, string? handle)
    {
        var name = msg.SenderName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = handle is null ? null : "@" + handle;
        }

        name = string.IsNullOrEmpty(name) ? "Guest" : name;
        return name.Length > 100 ? name[..100] : name;
    }

    internal static string CodeHash(string code)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    }
}
