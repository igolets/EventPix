using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

/// <summary>Either an e-mail, or a messenger invite (`channel` Telegram|Max + handle without the @).</summary>
internal sealed record AddInviteDto(string? Email, NotifyChannel? Channel, string? Handle);

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
                .OrderBy(i => i.CreatedUtc).ThenBy(i => i.Email).ThenBy(i => i.MessengerHandle)
                .Select(i => new
                {
                    i.Id,
                    i.Email,
                    i.MessengerChannel,
                    i.MessengerHandle,
                    i.CreatedUtc,
                    Joined = db.Members.Any(m => m.EventId == i.EventId &&
                        (i.Email != null && m.Email == i.Email ||
                         i.MessengerHandle != null && m.MessengerHandle == i.MessengerHandle)),
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

            var now = clock.GetUtcNow().UtcDateTime;
            Invite invite;
            if (dto.Email is null && dto.Channel is NotifyChannel.Telegram or NotifyChannel.Max)
            {
                // Messenger invite: the bot matches the handle on the member's first Start.
                var handle = Tokens.NormalizeHandle(dto.Handle);
                if (handle is null)
                {
                    return Results.BadRequest(new { error = "invalid_handle" });
                }

                if (await db.Members.AnyAsync(
                        m => m.EventId == ev.Id && m.MessengerChannel == dto.Channel && m.MessengerHandle == handle, ct))
                {
                    return Results.Conflict(new { error = "already_member" });
                }

                if (await db.Invites.AnyAsync(
                        i => i.EventId == ev.Id && i.MessengerChannel == dto.Channel && i.MessengerHandle == handle, ct))
                {
                    return Results.Conflict(new { error = "already_invited" });
                }

                invite = new Invite { EventId = ev.Id, MessengerChannel = dto.Channel, MessengerHandle = handle, CreatedUtc = now };
            }
            else
            {
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

                invite = new Invite { EventId = ev.Id, Email = email, CreatedUtc = now };
            }

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
