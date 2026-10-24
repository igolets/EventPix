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
    /// </summary>
    public static RouteGroupBuilder WithEventScope(this RouteGroupBuilder group, bool requireMember)
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
            if (requireMember && member is null)
            {
                return Results.Unauthorized();
            }

            http.Items[Key] = new EventScope(ev, member);
            return await next(ctx);
        });
        return group;
    }
}
