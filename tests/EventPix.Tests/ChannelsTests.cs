using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventPix.Tests;

public sealed class ChannelsTests
{
    private const string Url = "http://localhost:5000/e/tok";
    private const string Unsub = "http://localhost:5000/u/abc.def";

    [Fact]
    public void Composer_Instant_Text_Contains_Summary_Links_And_Unsubscribe()
    {
        var groups = new List<ContentGroup> { new("Ceremony", "Ana", 3, 1) };
        var text = Composer.Body(groups, Url, Unsub);
        Assert.Contains("Ana added 3 photo(s) and 1 video(s)", text, StringComparison.Ordinal);
        Assert.Contains(Url, text, StringComparison.Ordinal);
        Assert.Contains(Unsub, text, StringComparison.Ordinal);
        Assert.Equal("New in Wedding", Composer.Subject("Wedding", groups));
    }

    [Fact]
    public void Composer_Digest_Lists_Every_Group_And_Labels_The_Count()
    {
        var groups = new List<ContentGroup> { new("Ceremony", "Ana", 3, 1), new("Party", "Bob", 0, 2) };
        var text = Composer.Body(groups, Url, Unsub);
        Assert.Contains("Ana added 3 photo(s) and 1 video(s)", text, StringComparison.Ordinal);
        Assert.Contains("Bob added 0 photo(s) and 2 video(s)", text, StringComparison.Ordinal);
        Assert.Contains(Url, text, StringComparison.Ordinal);
        Assert.Equal("New in Wedding (2 updates)", Composer.Subject("Wedding", groups));
    }

    [Fact]
    public void Composer_Json_Round_Trips_Groups()
    {
        var groups = new List<ContentGroup> { new("Ceremony", "Ana", 3, 1), new("Party", "Bob", 0, 2) };
        var back = Composer.Deserialize(Composer.Serialize(groups));
        Assert.Equal(groups, back);
    }

    [Fact]
    public void Composer_Null_Payload_Yields_Empty_Groups()
    {
        Assert.Empty(Composer.Deserialize("null"));
    }

    [Fact]
    public async Task EmailChannel_Forwards_To_The_Configured_Sender()
    {
        var channel = new EmailChannel(new LogEmailSender(NullLogger<LogEmailSender>.Instance));
        await channel.SendAsync("guest@example.com", "New in Wedding", "body", CancellationToken.None);
        Assert.Equal(NotifyChannel.Email, channel.Channel);
    }

    [Fact]
    public async Task TelegramChannel_Without_Token_Is_A_Silent_NoOp()
    {
        var channel = new TelegramChannel(Options.Create(new TelegramOptions()));
        await channel.SendAsync("12345", "New in Wedding", "body", CancellationToken.None);
        Assert.Equal(NotifyChannel.Telegram, channel.Channel);
    }

    [Fact]
    public void Link_Code_Hash_Is_Stable_Sha256_Hex()
    {
        var first = TelegramUpdateWorker.CodeHash("abc");
        Assert.Equal(first, TelegramUpdateWorker.CodeHash("abc"));
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, TelegramUpdateWorker.CodeHash("abd"));
    }
}
