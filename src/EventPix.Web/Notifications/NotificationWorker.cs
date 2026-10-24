using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Notifications;

/// <summary>
/// Every minute: batch media that became Ready and is older than the debounce window,
/// group by (tab, uploader) and notify matching subscribers (never the uploader).
/// Delivery is at-most-once. TODO: Digest mode (hourly/daily rollup) and retry/outbox.
/// </summary>
internal sealed class NotificationWorker(
    IServiceScopeFactory scopes, IOptions<EventPixOptions> options, TimeProvider clock,
    ILogger<NotificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.TickFailed(e); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var pepper = sp.GetRequiredService<SecretPepper>();
        var tokens = sp.GetRequiredService<EventTokenService>();
        var channels = sp.GetServices<INotificationChannel>().ToDictionary(c => c.Channel);

        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddMinutes(-options.Value.NotifyDebounceMinutes);
        var pending = await db.MediaItems
            .Where(m => m.Status == MediaStatus.Ready && m.NotifiedUtc == null && m.UploadedUtc <= cutoff)
            .ToListAsync(ct);

        foreach (var batch in pending.GroupBy(m => (m.EventId, m.TabId, m.UploaderId)))
        {
            var (eventId, tabId, uploaderId) = batch.Key;
            var ev = await db.Events.FirstAsync(e => e.Id == eventId, ct);
            var tab = await db.Tabs.FirstAsync(t => t.Id == tabId, ct);
            var uploader = await db.Members.FirstAsync(m => m.Id == uploaderId, ct);
            var subs = await db.Subscriptions.Where(s =>
                s.EventId == eventId && s.Confirmed && s.MemberId != uploaderId &&
                (s.TabId == null || s.TabId == tabId)).ToListAsync(ct);

            var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
            var link = tokens.Url(ev, baseUrl);
            foreach (var s in subs)
            {
                if (!channels.TryGetValue(s.Channel, out var channel))
                {
                    continue;
                }

                var unsub = $"{baseUrl}/u/{s.Id:N}.{pepper.Hmac("unsub|" + s.Id.ToString("N"))}";
                var msg = new NewContentMessage(ev.Name, tab.Name, uploader.DisplayName,
                    batch.Count(m => m.Kind == MediaKind.Photo), batch.Count(m => m.Kind == MediaKind.Video), link, unsub);
                try { await channel.SendAsync(s.Address, msg, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                { log.NotifyFailed(e, s.Channel, s.Id); }
            }
            foreach (var m in batch)
            {
                m.NotifiedUtc = now;
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
