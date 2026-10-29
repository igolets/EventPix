using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using EventPix.Web.Localization;
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
        var text = Composer.Body(MessageLanguage.English, groups, Url, Unsub);
        Assert.Contains("Ana added 3 photo(s) and 1 video(s)", text, StringComparison.Ordinal);
        Assert.Contains(Url, text, StringComparison.Ordinal);
        Assert.Contains(Unsub, text, StringComparison.Ordinal);
        Assert.Equal("New in Wedding", Composer.Subject(MessageLanguage.English, "Wedding", groups));
    }

    [Fact]
    public void Composer_Separates_The_Links_With_A_Blank_Line()
    {
        var groups = new List<ContentGroup> { new("Ceremony", "Ana", 3, 1) };
        var text = Composer.Body(MessageLanguage.English, groups, Url, Unsub);
        Assert.Contains(Url + "\n\nUnsubscribe: " + Unsub, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_Digest_Lists_Every_Group_And_Labels_The_Count()
    {
        var groups = new List<ContentGroup> { new("Ceremony", "Ana", 3, 1), new("Party", "Bob", 0, 2) };
        var text = Composer.Body(MessageLanguage.English, groups, Url, Unsub);
        Assert.Contains("Ana added 3 photo(s) and 1 video(s)", text, StringComparison.Ordinal);
        Assert.Contains("Bob added 0 photo(s) and 2 video(s)", text, StringComparison.Ordinal);
        Assert.Contains(Url, text, StringComparison.Ordinal);
        Assert.Equal("New in Wedding (2 updates)", Composer.Subject(MessageLanguage.English, "Wedding", groups));
    }

    [Fact]
    public void Composer_Localises_The_Texts_For_The_Configured_Language()
    {
        var groups = new List<ContentGroup> { new("Церемония", "Аня", 3, 1) };
        Assert.Equal("Новое в «Свадьба»", Composer.Subject(MessageLanguage.Russian, "Свадьба", groups));
        Assert.Equal("Новое в «Свадьба» — обновлений: 2",
            Composer.Subject(MessageLanguage.Russian, "Свадьба", [.. groups, new ContentGroup("Вечеринка", "Боб", 0, 2)]));

        var text = Composer.Body(MessageLanguage.Russian, groups, Url, Unsub);
        Assert.Contains("Новые материалы в альбоме «Церемония» от Аня: 3 фото, 1 видео.", text, StringComparison.Ordinal);
        Assert.Contains("Открыть: " + Url, text, StringComparison.Ordinal);
        Assert.Contains("Отписаться: " + Unsub, text, StringComparison.Ordinal);
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
    public async Task MaxChannel_Without_Token_Is_A_Silent_NoOp()
    {
        var channel = new MaxChannel([new StubGateway(NotifyChannel.Max)]);
        await channel.SendAsync("12345", "New in Wedding", "body", CancellationToken.None);
        Assert.Equal(NotifyChannel.Max, channel.Channel);
    }

    [Fact]
    public void MaxChannel_Without_A_Max_Gateway_Is_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new MaxChannel([new StubGateway(NotifyChannel.Telegram)]));
    }

    [Fact]
    public async Task MaxChannel_Delivers_Through_The_Max_Gateway()
    {
        var gateway = new StubGateway(NotifyChannel.Max);
        var channel = new MaxChannel([gateway]);
        await channel.SendAsync("12345", "New in Wedding", "the body", CancellationToken.None);
        var sent = Assert.Single(gateway.Sent);
        Assert.Equal(12345, sent.ChatId);
        Assert.Equal("the body", sent.Text);
    }

    [Fact]
    public void Link_Code_Hash_Is_Stable_Sha256_Hex()
    {
        var first = BotLinkWorker.CodeHash("abc");
        Assert.Equal(first, BotLinkWorker.CodeHash("abc"));
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, BotLinkWorker.CodeHash("abd"));
    }

    private sealed class StubGateway(NotifyChannel channel) : IBotLinkGateway
    {
        public NotifyChannel Channel { get; } = channel;

        public List<(long ChatId, string Text)> Sent { get; } = [];

        public Task<string?> GetBotUsernameAsync(CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task SendAsync(long chatId, string text, CancellationToken ct)
        {
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public string BuildLinkUrl(string botName, string code)
        {
            return botName + code;
        }

        public Task ReceiveAsync(Func<BotMessage, CancellationToken, Task> onMessage, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
