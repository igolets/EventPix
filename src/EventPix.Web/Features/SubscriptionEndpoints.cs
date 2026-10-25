using System.Buffers.Text;
using System.Security.Cryptography;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

internal sealed record SubscribeDto(NotifyChannel Channel, Guid? TabId, NotifyMode Mode);

internal static class SubscriptionEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/subscriptions", async (HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            return Results.Ok(await db.Subscriptions.AsNoTracking()
                .Where(s => s.EventId == ev.Id && s.MemberId == me!.Id)
                .Select(s => new { s.Id, s.Channel, s.TabId, s.Mode, s.Confirmed }).ToListAsync(ct));
        });

        g.MapPut("/subscriptions", async (SubscribeDto dto, HttpContext h, EventPixDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (dto.Channel != NotifyChannel.Email)
            {
                // Telegram subscriptions can only be confirmed by the bot proving the chat id.
                return Results.Json(new { error = "telegram_use_link" }, statusCode: 400);
            }

            if (dto.TabId is not null && !await db.Tabs.AnyAsync(t => t.Id == dto.TabId && t.EventId == ev.Id, ct))
            {
                return Results.NotFound();
            }

            var sub = await db.Subscriptions.FirstOrDefaultAsync(s =>
                s.EventId == ev.Id && s.MemberId == me!.Id && s.Channel == dto.Channel && s.TabId == dto.TabId, ct);
            if (sub is null)
            {
                sub = new Subscription
                {
                    EventId = ev.Id,
                    MemberId = me!.Id,
                    Channel = dto.Channel,
                    Address = me.Email,
                    TabId = dto.TabId,
                    Confirmed = true,   // the address was already proven via the sign-in code
                    CreatedUtc = clock.GetUtcNow().UtcDateTime,
                };
                db.Subscriptions.Add(sub);
            }
            sub.Mode = dto.Mode;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { sub.Id });
        });

        // Issues a single-use, 15-minute code the user sends to the bot as a /start deep link.
        g.MapGet("/subscriptions/telegram-link", async (
            HttpContext h, EventPixDb db, ITelegramBotGateway telegram, TimeProvider clock, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var botName = await telegram.GetBotUsernameAsync(ct);
            if (string.IsNullOrEmpty(botName))
            {
                return Results.Json(new { error = "telegram_not_configured" }, statusCode: 501);
            }

            Guid? tabId = null;
            if (Guid.TryParse(h.Request.Query["tab"], out var parsedTab))
            {
                tabId = parsedTab;
            }

            if (tabId is not null && !await db.Tabs.AnyAsync(t => t.Id == tabId && t.EventId == ev.Id, ct))
            {
                return Results.NotFound();
            }

            var mode = Enum.TryParse<NotifyMode>(h.Request.Query["mode"], ignoreCase: true, out var parsedMode)
                ? parsedMode : NotifyMode.Instant;

            var now = clock.GetUtcNow().UtcDateTime;
            var expires = now.AddMinutes(15);
            await db.TelegramLinkCodes
                .Where(c => c.ExpiresUtc < now || c.ConsumedUtc != null).ExecuteDeleteAsync(ct);

            var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            db.TelegramLinkCodes.Add(new TelegramLinkCode
            {
                CodeHash = TelegramUpdateWorker.CodeHash(code),
                EventId = ev.Id,
                MemberId = me!.Id,
                TabId = tabId,
                Mode = mode,
                CreatedUtc = now,
                ExpiresUtc = expires,
            });
            await db.SaveChangesAsync(ct);

            var url = $"https://t.me/{botName}?start={code}";
            return Results.Ok(new { url, expiresUtc = expires });
        });

        g.MapDelete("/subscriptions/{id:guid}", async (Guid id, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var n = await db.Subscriptions.Where(s => s.Id == id && s.EventId == ev.Id && s.MemberId == me!.Id).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    /// <summary>One-click unsubscribe. Link carries an HMAC so ids can't be forged; pending outbox rows for
    /// the subscription die with it (cascade).</summary>
    public static void MapUnsubscribe(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/unsubscribe/{id:guid}/{sig}", async (Guid id, string sig, EventPixDb db, SecretPepper pepper, CancellationToken ct) =>
        {
            if (!pepper.Verify("unsub|" + id.ToString("N"), sig))
            {
                return Results.NotFound();
            }

            await db.Subscriptions.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }).RequireRateLimiting("auth");
    }
}
