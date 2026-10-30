using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Localization;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Notifications;

/// <summary>
/// Every minute: (1) dispatch — batch media that became Ready and is older than the debounce window,
/// group by (tab, uploader) and enqueue one outbox row per matching subscription (never the uploader);
/// digest subscriptions get one accumulating row per rolling window instead. (2) deliver — send due
/// outbox rows, retrying failures with exponential backoff; exhausted rows are dropped with a warning.
/// Delivery is at-least-once.
/// </summary>
internal sealed class NotificationWorker(
    IServiceScopeFactory scopes, IOptions<EventPixOptions> options, TimeProvider clock,
    ILogger<NotificationWorker> log) : BackgroundService
{
    internal const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.TickFailed(e); }
        }
    }

    /// <summary>Internal for tests: runs one dispatch + deliver cycle synchronously.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var pepper = sp.GetRequiredService<SecretPepper>();
        var tokens = sp.GetRequiredService<EventTokenService>();
        var channels = sp.GetServices<INotificationChannel>().ToDictionary(c => c.Channel);

        await DispatchAsync(db, pepper, tokens, options.Value, ct);
        await DeliverAsync(db, channels, options.Value, ct);
    }

    private async Task DispatchAsync(
        EventPixDb db, SecretPepper pepper, EventTokenService tokens, EventPixOptions opts, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddMinutes(-opts.NotifyDebounceMinutes);
        var pending = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Ready && m.NotifiedUtc == null && m.UploadedUtc <= cutoff)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var batch in pending.GroupBy(m => (m.EventId, m.TabId, m.UploaderId)))
        {
            var (eventId, tabId, uploaderId) = batch.Key;
            var ev = await db.Events.FirstAsync(e => e.Id == eventId, ct);
            var tab = await db.Tabs.FirstAsync(t => t.Id == tabId, ct);
            var uploader = await db.Members.FirstAsync(m => m.Id == uploaderId, ct);
            var subs = await db.Subscriptions.Where(s =>
                s.EventId == eventId && s.Confirmed && s.MemberId != uploaderId &&
                (s.TabId == null || s.TabId == tabId)).ToListAsync(ct);
            if (subs.Count == 0)
            {
                foreach (var m in batch)
                {
                    m.NotifiedUtc = now;
                }

                continue;
            }

            var baseUrl = opts.PublicBaseUrl.TrimEnd('/');
            var link = tokens.Url(ev, baseUrl);
            var group = new ContentGroup(tab.Name, uploader.DisplayName,
                batch.Count(m => m.Kind == MediaKind.Photo), batch.Count(m => m.Kind == MediaKind.Video));

            foreach (var s in subs)
            {
                var unsub = $"{baseUrl}/u/{s.Id:N}.{pepper.Hmac("unsub|" + s.Id.ToString("N"))}";
                if (s.Mode == NotifyMode.Instant)
                {
                    db.Outbox.Add(Envelope(s, ev.Name, link, unsub, [group], now));
                }
                else
                {
                    var open = await db.Outbox.FirstOrDefaultAsync(
                        o => o.SubscriptionId == s.Id && o.SentUtc == null, ct);
                    if (open is null)
                    {
                        db.Outbox.Add(Envelope(s, ev.Name, link, unsub, [group],
                            now.AddHours(opts.NotifyDigestHours)));
                        continue;
                    }

                    // merge the new batch into the still-open digest row; the window rolls forward
                    var groups = Composer.Deserialize(open.GroupsJson);
                    var same = groups.FirstOrDefault(
                        g => g.Tab == tab.Name && g.Uploader == uploader.DisplayName);
                    if (same is null)
                    {
                        groups.Add(group);
                    }
                    else
                    {
                        groups[groups.IndexOf(same)] = same with
                        {
                            Photos = same.Photos + group.Photos,
                            Videos = same.Videos + group.Videos,
                        };
                    }

                    open.GroupsJson = Composer.Serialize(groups);
                    open.NotBeforeUtc = now.AddHours(opts.NotifyDigestHours);
                }
            }

            foreach (var m in batch)
            {
                m.NotifiedUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static OutboxMessage Envelope(
        Subscription s, string eventName, string link, string unsub, ContentGroup[] groups, DateTime notBefore)
    {
        return new OutboxMessage
        {
            SubscriptionId = s.Id,
            Channel = s.Channel,
            Address = s.Address,
            EventName = eventName,
            Url = link,
            UnsubscribeUrl = unsub,
            GroupsJson = Composer.Serialize(groups),
            NotBeforeUtc = notBefore,
        };
    }

    private async Task DeliverAsync(
        EventPixDb db, Dictionary<NotifyChannel, INotificationChannel> channels, EventPixOptions opts,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lang = Messages.Resolve(opts.Language);
        var due = await db.Outbox
            .Where(o => o.SentUtc == null && o.NotBeforeUtc <= now && o.Attempts < MaxAttempts)
            .OrderBy(o => o.Id)
            .Take(100)
            .ToListAsync(ct);

        foreach (var row in due)
        {
            if (!channels.TryGetValue(row.Channel, out var channel))
            {
                continue;   // channel not registered (e.g. no Telegram token configured)
            }

            try
            {
                var groups = Composer.Deserialize(row.GroupsJson);
                await channel.SendAsync(row.Address, Composer.Subject(lang, row.EventName, groups),
                    Composer.Body(lang, groups, row.Url, row.UnsubscribeUrl), ct);
                row.SentUtc = clock.GetUtcNow().UtcDateTime;
                row.Attempts++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                row.Attempts++;
                if (row.Attempts >= MaxAttempts)
                {
                    log.Dropped(e, row.Channel, row.SubscriptionId, row.Attempts);
                    db.Outbox.Remove(row);
                }
                else
                {
                    row.NotBeforeUtc = now + Backoff(row.Attempts);
                    log.RetryQueued(e, row.Channel, row.SubscriptionId, row.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    internal static TimeSpan Backoff(int attempts)
    {
        return TimeSpan.FromMinutes(Math.Min(60d, Math.Pow(2, attempts)));
    }
}
