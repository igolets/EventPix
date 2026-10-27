using System.Security.Claims;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Features;

internal sealed record RequestCodeDto(string? Email);
internal sealed record VerifyDto(string? Email, string? Code);
internal sealed record ProfileDto(string? DisplayName);

internal static class AuthEndpoints
{
    /// <summary>Mapped on a group that resolves the event but does NOT require a member.</summary>
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/public", (HttpContext h, IOptions<TelegramOptions> telegram) =>
        {
            var ev = h.GetScope().Event;
            // Capability flag, not a secret: lets the UI hide the Telegram channel while the bot is unconfigured.
            var hasBot = !string.IsNullOrWhiteSpace(telegram.Value.BotToken);
            return Results.Ok(new { ev.Name, ev.Description, ev.InviteOnly, Telegram = hasBot });
        });

        g.MapPost("/auth/request", async (RequestCodeDto dto, HttpContext h, OtpService otp, CancellationToken ct) =>
        {
            var email = Tokens.NormalizeEmail(dto.Email);
            if (email is null)
            {
                return Results.BadRequest(new { error = "invalid_email" });
            }

            await otp.RequestAsync(h.GetScope().Event, email, ct);
            return Results.Accepted();                       // same answer whether or not the address is known
        }).RequireRateLimiting("auth");

        g.MapPost("/auth/verify", async (VerifyDto dto, HttpContext h, OtpService otp, CancellationToken ct) =>
        {
            var email = Tokens.NormalizeEmail(dto.Email);
            if (email is null || string.IsNullOrWhiteSpace(dto.Code) || dto.Code.Length > 10)
            {
                return Results.BadRequest(new { error = "invalid_request" });
            }

            var ev = h.GetScope().Event;
            var member = await otp.VerifyAsync(ev, email, dto.Code, ct);
            if (member is null)
            {
                return Results.Json(new { error = "invalid_code" }, statusCode: 401);
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim("mid", member.Id.ToString()), new Claim("eid", ev.Id.ToString()) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            await h.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        g.MapPost("/auth/logout", async (HttpContext h) =>
        {
            await h.SignOutAsync();
            return Results.NoContent();
        });

        g.MapGet("/me", (HttpContext h) =>
        {
            var m = h.GetScope().Member;
            return m is null ? Results.Unauthorized()
                : Results.Ok(new { m.Id, m.Email, m.DisplayName, IsOwner = m.Role == Domain.MemberRole.Owner });
        });

        g.MapPut("/me", async (ProfileDto dto, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var scope = h.GetScope();
            if (scope.Member is null)
            {
                return Results.Unauthorized();
            }

            var name = dto.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                return Results.BadRequest(new { error = "invalid_name" });
            }

            await db.Members.Where(m => m.Id == scope.Member.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DisplayName, name), ct);
            return Results.NoContent();
        });
    }
}
