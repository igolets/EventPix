using System.Net;
using System.Net.Http.Json;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Localization;
using EventPix.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventPix.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("ru", true)]
    [InlineData("ru-RU", true)]
    [InlineData("RU", true)]
    [InlineData("en", false)]
    [InlineData("en-GB", false)]
    [InlineData("fr", false)]
    [InlineData("russian", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Resolve_Matches_The_Tag_And_Falls_Back_To_English(string? tag, bool russian)
    {
        Assert.Equal(
            russian ? MessageLanguage.Russian : MessageLanguage.English,
            Messages.Resolve(tag));
    }

    [Fact]
    public void Otp_Mail_Speaks_Both_Languages()
    {
        Assert.Equal("Your sign-in code for Wedding", Messages.OtpSubject(MessageLanguage.English, "Wedding"));
        Assert.Contains("Your code: 123456", Messages.OtpBody(MessageLanguage.English, "123456", 10), StringComparison.Ordinal);
        Assert.Equal("Код для входа в «Свадьба»", Messages.OtpSubject(MessageLanguage.Russian, "Свадьба"));
        Assert.Contains("Ваш код: 123456", Messages.OtpBody(MessageLanguage.Russian, "123456", 10), StringComparison.Ordinal);
        Assert.Contains("10 мин", Messages.OtpBody(MessageLanguage.Russian, "123456", 10), StringComparison.Ordinal);
    }

    [Fact]
    public void Bot_Texts_Localise_Scope_And_Cadence()
    {
        Assert.Equal("все альбомы", Messages.BotScopeAll(MessageLanguage.Russian));
        Assert.Equal("удалённый альбом", Messages.BotScopeRemoved(MessageLanguage.Russian));
        Assert.Equal("альбом «Венчание»", Messages.BotScopeTab(MessageLanguage.Russian, "Венчание"));
        Assert.Equal("раз в день дайджестом", Messages.BotCadence(MessageLanguage.Russian, NotifyMode.Digest));
        Assert.Equal("сразу после добавления", Messages.BotCadence(MessageLanguage.Russian, NotifyMode.Instant));
        Assert.Equal("Готово! Буду сообщать о новых материалах в «Свадьба» (все альбомы) раз в день дайджестом.",
            Messages.BotSubscribed(MessageLanguage.Russian, "Свадьба", Messages.BotScopeAll(MessageLanguage.Russian),
                Messages.BotCadence(MessageLanguage.Russian, NotifyMode.Digest)));
    }

    [Fact]
    public async Task Otp_Mail_Follows_The_Configured_Language()
    {
        using var app = new TestApp(configureSettings: b =>
            b.UseSetting($"{EventPixOptions.Section}:Language", "ru"));
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (token, _) = await app.CreateEventAsync(owner);

        var response = await app.CreateClient().PostAsJsonAsync($"/api/e/{token}/auth/request", new { Email = owner });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(app.Factory.Logs.Lines, l => l.Contains("Ваш код:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bot_Replies_Follow_The_Configured_Language()
    {
        var telegram = new StubGateway();
        using var app = new TestApp(
            s => s.AddSingleton<IBotLinkGateway>(telegram),
            b => b.UseSetting($"{EventPixOptions.Section}:Language", "ru"));
        var owner = "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
        var (_, eventId) = await app.CreateEventAsync(owner);
        Guid memberId;
        using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            memberId = await db.Members.Where(m => m.EventId == eventId).Select(m => m.Id).SingleAsync();
        }

        var code = await MintCodeAsync(app, eventId, memberId, NotifyMode.Digest);

        var worker = app.Factory.Services.GetServices<IHostedService>().OfType<BotLinkWorker>().Single();
        await worker.OnUpdateAsync(telegram, Msg(777, "/start " + code), CancellationToken.None);

        var message = Assert.Single(telegram.Sent);
        Assert.Contains("Готово!", message.Text, StringComparison.Ordinal);
        Assert.Contains("«Test event» (все альбомы) раз в день дайджестом.", message.Text, StringComparison.Ordinal);
    }

    private static async Task<string> MintCodeAsync(TestApp app, Guid eventId, Guid memberId, NotifyMode mode)
    {
        var now = DateTime.UtcNow;
        var code = Guid.NewGuid().ToString("N");
        using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        db.BotLinkCodes.Add(new BotLinkCode
        {
            CodeHash = BotLinkWorker.CodeHash(code),
            Channel = NotifyChannel.Telegram,
            EventId = eventId,
            MemberId = memberId,
            TabId = null,
            Mode = mode,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(15),
        });
        await db.SaveChangesAsync();
        return code;
    }

    private static BotMessage Msg(long chatId, string? text)
    {
        return new BotMessage(chatId, chatId, null, null, text);
    }

    private sealed class StubGateway : IBotLinkGateway
    {
        public NotifyChannel Channel => NotifyChannel.Telegram;

        public string? Username { get; set; } = "eventpix_bot";

        public List<(long ChatId, string Text)> Sent { get; } = [];

        public Task<string?> GetBotUsernameAsync(CancellationToken ct)
        {
            return Task.FromResult(Username);
        }

        public Task SendAsync(long chatId, string text, CancellationToken ct)
        {
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public string BuildLinkUrl(string botName, string code)
        {
            return $"https://t.me/{botName}?start={code}";
        }

        public Task ReceiveAsync(Func<BotMessage, CancellationToken, Task> onMessage, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
