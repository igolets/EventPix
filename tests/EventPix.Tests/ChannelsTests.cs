using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventPix.Tests;

public sealed class ChannelsTests
{
    private static readonly NewContentMessage _message =
        new("Wedding", "Ceremony", "Ana", 3, 1, "http://localhost:5000/e/tok", "http://localhost:5000/u/abc.def");

    [Fact]
    public void Message_Text_Contains_Summary_Links_And_Unsubscribe()
    {
        var text = _message.ToText();
        Assert.Contains("Ana added 3 photo(s) and 1 video(s)", text, StringComparison.Ordinal);
        Assert.Contains(_message.Url, text, StringComparison.Ordinal);
        Assert.Contains(_message.UnsubscribeUrl, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailChannel_Forwards_To_The_Configured_Sender()
    {
        var channel = new EmailChannel(new LogEmailSender(NullLogger<LogEmailSender>.Instance));
        await channel.SendAsync("guest@example.com", _message, CancellationToken.None);   // dev sender logs only
        Assert.Equal(NotifyChannel.Email, channel.Channel);
    }

    [Fact]
    public async Task TelegramChannel_Without_Token_Is_A_Silent_NoOp()
    {
        var channel = new TelegramChannel(Options.Create(new TelegramOptions()));
        await channel.SendAsync("12345", _message, CancellationToken.None);
        Assert.Equal(NotifyChannel.Telegram, channel.Channel);
    }
}
