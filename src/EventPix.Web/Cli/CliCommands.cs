using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Cli;

/// <summary>
/// Operator commands. Event management is deliberately CLI-only in v1: no admin web surface to attack.
///   create-event "Name" owner@example.com [--invite-only]
///   list-events
///   rotate-token {eventId}      (invalidates the old link, prints the new one)
/// </summary>
internal static class CliCommands
{
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0] is "create-event" or "list-events" or "rotate-token";

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var tokens = scope.ServiceProvider.GetRequiredService<EventTokenService>();
        var baseUrl = scope.ServiceProvider.GetRequiredService<IOptions<EventPixOptions>>().Value.PublicBaseUrl;

        switch (args[0])
        {
            case "create-event":
                {
                    var email = args.Length > 2 ? Tokens.NormalizeEmail(args[2]) : null;
                    if (args.Length < 3 || email is null || string.IsNullOrWhiteSpace(args[1]))
                    { await Console.Error.WriteLineAsync("Usage: create-event \"Name\" owner@example.com [--invite-only]"); return 2; }

                    var (token, hash, prot) = tokens.Create();
                    var now = DateTime.UtcNow;
                    var ev = new Event
                    {
                        Name = args[1].Trim(),
                        OwnerEmail = email,
                        TokenHash = hash,
                        TokenProtected = prot,
                        InviteOnly = args.Contains("--invite-only"),
                        CreatedUtc = now,
                    };
                    db.Events.Add(ev);
                    db.Members.Add(new Member { EventId = ev.Id, Email = email, DisplayName = email.Split('@')[0], Role = MemberRole.Owner, CreatedUtc = now });
                    await db.SaveChangesAsync();
                    await Console.Out.WriteLineAsync($"Event {ev.Id} created.\nShare this secret link: {baseUrl.TrimEnd('/')}/e/{token}");
                    return 0;
                }
            case "list-events":
                foreach (var e in await db.Events.AsNoTracking().OrderBy(e => e.CreatedUtc).ToListAsync())
                {
                    await Console.Out.WriteLineAsync($"{e.Id}  {e.Name}  owner={e.OwnerEmail}  {tokens.Url(e, baseUrl)}");
                }

                return 0;
            case "rotate-token":
                {
                    if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) { await Console.Error.WriteLineAsync("Usage: rotate-token {eventId}"); return 2; }
                    var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id);
                    if (ev is null) { await Console.Error.WriteLineAsync("Not found."); return 1; }
                    var (token, hash, prot) = tokens.Create();
                    ev.TokenHash = hash; ev.TokenProtected = prot;
                    await db.SaveChangesAsync();
                    await Console.Out.WriteLineAsync($"New link: {baseUrl.TrimEnd('/')}/e/{token}");
                    return 0;
                }
        }
        return 2;
    }
}
