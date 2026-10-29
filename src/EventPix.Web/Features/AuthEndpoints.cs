using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using EventPix.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Features;

internal sealed record RequestCodeDto(string? Email);
internal sealed record VerifyDto(string? Email, string? Code);
internal sealed record ProfileDto(string? DisplayName);
internal sealed record BotLoginRequestDto(NotifyChannel Channel);
internal sealed record BotLoginPollDto(string? Code);

internal static class AuthEndpoints
{
    /// <summary>Mapped on a group that resolves the event but does NOT require a member.</summary>
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/public", (HttpContext h, IOptions<TelegramOptions> telegram, IOptions<MaxOptions> max) =>
        {
            var ev = h.GetScope().Event;
            // Capability flags, not secrets: let the UI hide bot channels while their bot is unconfigured.
            var hasTelegram = !string.IsNullOrWhiteSpace(telegram.Value.BotToken);
            var hasMax = !string.IsNullOrWhiteSpace(max.Value.BotToken);
            return Results.Ok(new { ev.Name, ev.Description, ev.InviteOnly, Telegram = hasTelegram, Max = hasMax });
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

        // Passwordless sign-in through a bot. The browser mints a single-use code and shows its deep
        // link; pressing Start in the bot proves the chat's messenger identity; the browser polls
        // until the request is confirmed and then receives the same session cookie as the OTP flow.
        g.MapPost("/auth/bot/request", async (
            BotLoginRequestDto dto, HttpContext h, EventPixDb db, IEnumerable<IBotLinkGateway> gateways,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (dto.Channel is not (NotifyChannel.Telegram or NotifyChannel.Max))
            {
                return Results.Json(new { error = "unknown_channel" }, statusCode: 400);
            }

            // Last-wins mirrors DI single-service resolution, so a later gateway overrides a bot.
            var gateway = gateways.LastOrDefault(x => x.Channel == dto.Channel);
            var botName = gateway is null ? null : await gateway.GetBotUsernameAsync(ct);
            if (string.IsNullOrEmpty(botName))
            {
                return Results.Json(new { error = "bot_not_configured" }, statusCode: 501);
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var expires = now.AddMinutes(15);
            await db.LoginRequests
                .Where(r => r.ExpiresUtc < now || r.ConsumedUtc != null).ExecuteDeleteAsync(ct);

            var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            db.LoginRequests.Add(new LoginRequest
            {
                CodeHash = BotLinkWorker.CodeHash(code),
                Channel = dto.Channel,
                EventId = h.GetScope().Event.Id,
                CreatedUtc = now,
                ExpiresUtc = expires,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { url = gateway!.BuildLinkUrl(botName, code), expiresUtc = expires });
        }).RequireRateLimiting("auth");

        // Uniform answer: unknown, expired, unconfirmed and already-consumed codes are all "pending",
        // so polling reveals nothing about which codes exist. Only the browser that minted the code
        // (and the chat that confirmed it) can ever see the success status; the request is consumed
        // exactly once, so a second poller cannot ride along.
        g.MapPost("/auth/bot/poll", async (
            BotLoginPollDto dto, HttpContext h, EventPixDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var ev = h.GetScope().Event;
            var code = dto.Code;
            if (string.IsNullOrWhiteSpace(code) || code.Length > 128)
            {
                return Results.Ok(new { status = "pending" });
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var hash = BotLinkWorker.CodeHash(code);
            var claimed = await db.LoginRequests.Where(
                r => r.CodeHash == hash && r.EventId == ev.Id && r.MemberId != null &&
                     r.ConsumedUtc == null && r.ExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedUtc, now), ct);
            if (claimed == 0)
            {
                return Results.Ok(new { status = "pending" });
            }

            var memberId = await db.LoginRequests.Where(r => r.CodeHash == hash && r.EventId == ev.Id)
                .Select(r => r.MemberId!.Value).FirstAsync(ct);
            var member = await db.Members.FirstAsync(m => m.Id == memberId && m.EventId == ev.Id, ct);
            member.LastSeenUtc = now;
            await db.SaveChangesAsync(ct);

            var identity = new ClaimsIdentity(
                new[] { new Claim("mid", member.Id.ToString()), new Claim("eid", ev.Id.ToString()) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            await h.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.NoContent();
        }).RequireRateLimiting("bot-poll");

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
