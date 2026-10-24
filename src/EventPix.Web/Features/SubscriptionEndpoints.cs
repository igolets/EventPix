using EventPix.Web.Data;
using EventPix.Web.Domain;
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
                return Results.Json(new { error = "channel_not_linked" }, statusCode: 501);   // TODO: Telegram deep-link flow
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

        g.MapDelete("/subscriptions/{id:guid}", async (Guid id, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var n = await db.Subscriptions.Where(s => s.Id == id && s.EventId == ev.Id && s.MemberId == me!.Id).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    /// <summary>One-click unsubscribe from e-mail. Link carries an HMAC so ids can't be forged.</summary>
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
