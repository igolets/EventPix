using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

internal sealed record AddInviteDto(string? Email);

/// <summary>Owner-only management of the pre-registered invite list for InviteOnly events.</summary>
internal static class InviteEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/invites", async (HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (me!.Role != MemberRole.Owner)
            {
                return Results.StatusCode(403);
            }

            var invites = await db.Invites.AsNoTracking().Where(i => i.EventId == ev.Id)
                .OrderBy(i => i.CreatedUtc).ThenBy(i => i.Email)
                .Select(i => new
                {
                    i.Id,
                    i.Email,
                    i.CreatedUtc,
                    Joined = db.Members.Any(m => m.EventId == i.EventId && m.Email == i.Email),
                })
                .ToListAsync(ct);
            return Results.Ok(invites);
        }).RequireMember();

        g.MapPost("/invites", async (AddInviteDto dto, HttpContext h, EventPixDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (me!.Role != MemberRole.Owner)
            {
                return Results.StatusCode(403);
            }

            var email = Tokens.NormalizeEmail(dto.Email);
            if (email is null)
            {
                return Results.BadRequest(new { error = "invalid_email" });
            }

            if (await db.Members.AnyAsync(m => m.EventId == ev.Id && m.Email == email, ct))
            {
                return Results.Conflict(new { error = "already_member" });
            }

            if (await db.Invites.AnyAsync(i => i.EventId == ev.Id && i.Email == email, ct))
            {
                return Results.Conflict(new { error = "already_invited" });
            }

            var invite = new Invite { EventId = ev.Id, Email = email, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Invites.Add(invite);
            await db.SaveChangesAsync(ct);
            return Results.Created($"invites/{invite.Id}", new { invite.Id });
        }).RequireMember();

        g.MapDelete("/invites/{id:guid}", async (Guid id, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (me!.Role != MemberRole.Owner)
            {
                return Results.StatusCode(403);
            }

            var n = await db.Invites.Where(i => i.Id == id && i.EventId == ev.Id).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireMember();
    }
}
