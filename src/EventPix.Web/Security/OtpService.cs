using System.Security.Cryptography;
using System.Text;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Localization;
using EventPix.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Security;

/// <summary>Passwordless e-mail codes. Codes are short-lived, HMAC-hashed and attempt-limited.</summary>
internal sealed class OtpService(
    EventPixDb db, SecretPepper pepper, IEmailSender mail,
    IOptions<EventPixOptions> options, TimeProvider clock)
{
    private const int MaxAttempts = 5;
    private const int MaxCodesPer10Min = 3;

    private string Hash(Event ev, string email, string code)
    {
        return pepper.Hmac($"{ev.Id:N}|{email}|{code}");
    }

    /// <summary>Never reveals whether the e-mail is known; callers always answer 202.</summary>
    public async Task RequestAsync(Event ev, string email, CancellationToken ct)
    {
        var known = await db.Members.AnyAsync(m => m.EventId == ev.Id && m.Email == email, ct) ||
                    await db.Invites.AnyAsync(i => i.EventId == ev.Id && i.Email == email, ct);
        if (!known && ev.InviteOnly)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var recent = await db.LoginCodes.CountAsync(
            c => c.EventId == ev.Id && c.Email == email && c.CreatedUtc > now.AddMinutes(-10), ct);
        if (recent >= MaxCodesPer10Min)
        {
            return;
        }

        var code = Tokens.NewOtp();
        db.LoginCodes.Add(new LoginCode
        {
            EventId = ev.Id,
            Email = email,
            CodeHash = Hash(ev, email, code),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(options.Value.OtpMinutes),
        });
        await db.SaveChangesAsync(ct);

        var lang = Messages.Resolve(options.Value.Language);
        await mail.SendAsync(email, Messages.OtpSubject(lang, ev.Name),
            Messages.OtpBody(lang, code, options.Value.OtpMinutes), ct);
    }

    public async Task<Member?> VerifyAsync(Event ev, string email, string code, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lc = await db.LoginCodes
            .Where(c => c.EventId == ev.Id && c.Email == email && c.ConsumedUtc == null && c.ExpiresUtc > now)
            .OrderByDescending(c => c.CreatedUtc).FirstOrDefaultAsync(ct);
        if (lc is null || lc.Attempts >= MaxAttempts)
        {
            return null;
        }

        lc.Attempts++;
        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(lc.CodeHash), Encoding.UTF8.GetBytes(Hash(ev, email, code.Trim())));
        if (!ok) { await db.SaveChangesAsync(ct); return null; }

        lc.ConsumedUtc = now;
        var member = await db.Members.FirstOrDefaultAsync(m => m.EventId == ev.Id && m.Email == email, ct);
        if (member is null)
        {
            var invited = await db.Invites.AnyAsync(i => i.EventId == ev.Id && i.Email == email, ct);
            if (ev.InviteOnly && !invited) { await db.SaveChangesAsync(ct); return null; }
            member = new Member
            {
                EventId = ev.Id,
                Email = email,
                DisplayName = email.Split('@')[0],
                Role = email == ev.OwnerEmail ? MemberRole.Owner : MemberRole.Guest,
                CreatedUtc = now,
            };
            db.Members.Add(member);
        }
        member.LastSeenUtc = now;
        await db.SaveChangesAsync(ct);
        return member;
    }
}
