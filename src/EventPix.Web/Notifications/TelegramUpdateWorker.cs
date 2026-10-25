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
/// When Telegram:BotToken is configured, long-polls the bot and turns "/start &lt;code&gt;" into a confirmed
/// Telegram subscription for the chat that sent it. Codes come from GET /subscriptions/telegram-link and are
/// single-use, short-lived and stored only as hashes. Without a token the worker exits immediately.
/// </summary>
internal sealed class TelegramUpdateWorker(
    IServiceScopeFactory scopes, IOptions<TelegramOptions> options, TimeProvider clock,
    ILogger<TelegramUpdateWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var bot = new TelegramBotClient(token);
        var receiver = new ReceiverOptions { AllowedUpdates = [UpdateType.Message] };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await bot.ReceiveAsync(HandleAsync, OnError, receiver, ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.PollingFailed(e);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    internal async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        var message = update.Message;
        if (message?.Text is not { } text || !text.StartsWith("/start", StringComparison.Ordinal))
        {
            return;
        }

        var code = text.Length > 6 ? text[6..].Trim() : "";
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var now = clock.GetUtcNow().UtcDateTime;

        // Codes are single-use and short-lived; drop the dead ones on every /start.
        await db.TelegramLinkCodes
            .Where(c => c.ExpiresUtc < now || c.ConsumedUtc != null)
            .ExecuteDeleteAsync(ct);

        var hash = CodeHash(code);
        var link = await db.TelegramLinkCodes.FirstOrDefaultAsync(
            c => c.CodeHash == hash && c.ConsumedUtc == null && c.ExpiresUtc > now, ct);
        if (link is null)
        {
            await bot.SendMessage(message.Chat.Id,
                "This linking code is not valid or has expired. " +
                "Open the album, tap Notify me and generate a new Telegram link.", cancellationToken: ct);
            return;
        }

        link.ConsumedUtc = now;
        var ev = await db.Events.FirstAsync(e => e.Id == link.EventId, ct);
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s =>
            s.EventId == link.EventId && s.MemberId == link.MemberId &&
            s.Channel == NotifyChannel.Telegram && s.TabId == link.TabId, ct);
        if (sub is null)
        {
            sub = new Subscription
            {
                EventId = link.EventId,
                MemberId = link.MemberId,
                Channel = NotifyChannel.Telegram,
                Address = message.Chat.Id.ToString(),
                TabId = link.TabId,
                Confirmed = true,   // the bot proves the chat id, like the sign-in code proves the e-mail
                CreatedUtc = now,
            };
            db.Subscriptions.Add(sub);
        }
        else
        {
            sub.Address = message.Chat.Id.ToString();
            sub.Confirmed = true;
        }

        await db.SaveChangesAsync(ct);
        await bot.SendMessage(message.Chat.Id,
            $"Done! I will tell you about new media in \u201c{ev.Name}\u201d.", cancellationToken: ct);
    }

    private Task OnError(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        log.PollingFailed(exception);
        return Task.CompletedTask;
    }

    internal static string CodeHash(string code)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    }
}
