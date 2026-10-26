using System.Security.Claims;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Security;

internal sealed record EventScope(Event Event, Member? Member);

internal static class EventScopeExtensions
{
    private const string Key = "eventpix.scope";

    public static EventScope GetScope(this HttpContext c)
    {
        return (EventScope)c.Items[Key]!;
    }

    /// <summary>
    /// Resolves {token} to an Event (404 if unknown) and, from the session cookie, the Member of THAT event.
    /// A cookie issued for another event never grants access here.
    /// Guest viewing (<paramref name="guestViewing"/>) lets anonymous callers read an open album; an
    /// <see cref="Domain.Event.InviteOnly"/> event keeps the login wall for viewing too.
    /// </summary>
    public static RouteGroupBuilder WithEventScope(this RouteGroupBuilder group, bool requireMember, bool guestViewing = false)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var token = http.Request.RouteValues["token"] as string;
            if (string.IsNullOrEmpty(token) || token.Length > 64)
            {
                return Results.NotFound();
            }

            var db = http.RequestServices.GetRequiredService<EventPixDb>();
            var hash = Tokens.Hash(token);
            var ev = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TokenHash == hash, http.RequestAborted);
            if (ev is null)
            {
                return Results.NotFound();
            }

            Member? member = null;
            if (Guid.TryParse(http.User.FindFirstValue("mid"), out var mid) &&
                Guid.TryParse(http.User.FindFirstValue("eid"), out var eid) && eid == ev.Id)
            {
                member = await db.Members.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == mid && m.EventId == ev.Id, http.RequestAborted);
            }
            if (member is null && (requireMember || (guestViewing && ev.InviteOnly)))
            {
                return Results.Unauthorized();
            }

            http.Items[Key] = new EventScope(ev, member);
            return await next(ctx);
        });
        return group;
    }

    /// <summary>Marks an endpoint as member-only (uploads, stars, management): anonymous callers get 401.</summary>
    public static RouteHandlerBuilder RequireMember(this RouteHandlerBuilder endpoint) =>
        endpoint.AddEndpointFilter(static async (ctx, next) =>
            ctx.HttpContext.GetScope().Member is null
                ? Results.Unauthorized()
                : await next(ctx));
}
