using System.Security.Cryptography;
using System.Text;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
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

    /// <summary>Long-polls text messages; returns immediately when no token is configured.</summary>
    public Task ReceiveAsync(Func<long, string?, CancellationToken, Task> onMessage, CancellationToken ct);
}

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

    public async Task ReceiveAsync(Func<long, string?, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var receiver = new ReceiverOptions { AllowedUpdates = [UpdateType.Message] };
        await new TelegramBotClient(token).ReceiveAsync(
            (_, update, _) => onMessage(update.Message?.Chat.Id ?? 0, update.Message?.Text, ct),
            (_, exception, _) => Task.FromException(exception),
            receiver, ct);
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
                await gateway.ReceiveAsync((chatId, text, c) => OnUpdateAsync(gateway, chatId, text, c), ct);
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
    internal Task OnUpdateAsync(IBotLinkGateway gateway, long chatId, string? text, CancellationToken ct)
    {
        return text is not null && text.StartsWith("/start", StringComparison.Ordinal)
            ? HandleStartAsync(gateway, chatId, text[6..].Trim(), ct)
            : Task.CompletedTask;
    }

    private async Task HandleStartAsync(IBotLinkGateway gateway, long chatId, string code, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var now = clock.GetUtcNow().UtcDateTime;

        // Codes are single-use and short-lived; drop the dead ones on every /start.
        await db.BotLinkCodes
            .Where(c => c.ExpiresUtc < now || c.ConsumedUtc != null)
            .ExecuteDeleteAsync(ct);

        var link = await db.BotLinkCodes.FirstOrDefaultAsync(
            c => c.CodeHash == CodeHash(code) && c.Channel == gateway.Channel &&
                 c.ConsumedUtc == null && c.ExpiresUtc > now, ct);
        if (link is null)
        {
            await gateway.SendAsync(chatId, InvalidCodeText, ct);
            return;
        }

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

    internal static string CodeHash(string code)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    }
}
