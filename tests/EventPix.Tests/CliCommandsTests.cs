using EventPix.Web.Cli;
using EventPix.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

public sealed class CliCommandsTests
{
    [Theory]
    [InlineData(true, "create-event")]
    [InlineData(true, "list-events")]
    [InlineData(true, "rotate-token")]
    [InlineData(true, "add-invites")]
    [InlineData(false, "nope")]
    [InlineData(false, "")]
    public void IsCommand_Matches_Known_Verbs(bool expected, string verb) =>
        Assert.Equal(expected, CliCommands.IsCommand([verb]));

    [Fact]
    public async Task AddInvites_Adds_New_And_Skips_Invalid_Members_Duplicates()
    {
        using var app = new TestApp();
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, eventId) = await app.CreateEventAsync(owner);   // owner e-mail already a Member

        var first = $"guest-{Guid.NewGuid():N}@example.com";
        var second = $"guest-{Guid.NewGuid():N}@example.com";
        Assert.Equal(0, await CliCommands.RunAsync(app.Factory.Services,
            ["add-invites", eventId.ToString(), first, "not-an-email", owner, second]));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var emails = await db.Invites.Where(i => i.EventId == eventId).Select(i => i.Email).ToListAsync();
            Assert.Equal(2, emails.Count);   // member and invalid address skipped
            Assert.Contains(first, emails);
            Assert.Contains(second, emails);
        }

        // Re-running skips everything already invited.
        Assert.Equal(0, await CliCommands.RunAsync(app.Factory.Services,
            ["add-invites", eventId.ToString(), first, second]));

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            Assert.Equal(2, await db.Invites.CountAsync(i => i.EventId == eventId));
        }
    }

    [Fact]
    public async Task AddInvites_Rejects_Bad_Usage_And_Unknown_Events()
    {
        using var app = new TestApp();
        Assert.Equal(2, await CliCommands.RunAsync(app.Factory.Services, ["add-invites", "not-a-guid"]));
        Assert.Equal(1, await CliCommands.RunAsync(app.Factory.Services,
            ["add-invites", Guid.NewGuid().ToString(), $"x-{Guid.NewGuid():N}@example.com"]));
    }

    [Fact]
    public async Task AddInvites_Preregisters_Messenger_Handles_In_Normalised_Form()
    {
        using var app = new TestApp();
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (_, eventId) = await app.CreateEventAsync(owner);

        Assert.Equal(0, await CliCommands.RunAsync(app.Factory.Services,
            ["add-invites", eventId.ToString(), "telegram:@Durov", "max:@grace", "telegram:bad handle!"]));

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var invites = await db.Invites.Where(i => i.EventId == eventId && i.MessengerHandle != null).ToListAsync();
        Assert.Equal(2, invites.Count);   // the malformed handle is skipped
        Assert.Contains(invites, i => i.MessengerChannel == EventPix.Web.Domain.NotifyChannel.Telegram && i.MessengerHandle == "durov");
        Assert.Contains(invites, i => i.MessengerChannel == EventPix.Web.Domain.NotifyChannel.Max && i.MessengerHandle == "grace");

        // Re-running skips what is already invited.
        Assert.Equal(0, await CliCommands.RunAsync(app.Factory.Services,
            ["add-invites", eventId.ToString(), "telegram:durov"]));
        Assert.Equal(2, await db.Invites.CountAsync(i => i.EventId == eventId && i.MessengerHandle != null));
    }
}
