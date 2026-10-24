using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Telegram.Bot;

namespace EventPix.Web.Notifications;

internal interface IEmailSender
{
    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct);
}

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        var o = options.Value;
        using var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(o.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = textBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port,
            o.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.User))
        {
            await client.AuthenticateAsync(o.User, o.Password, ct);
        }

        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Development fallback when no SMTP host is configured. Writes mail (incl. codes) to the log.</summary>
internal sealed class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
    {
        log.DevMail(to, subject, textBody);
        return Task.CompletedTask;
    }
}

internal sealed record NewContentMessage(
    string EventName, string TabName, string UploaderName, int Photos, int Videos, string Url, string UnsubscribeUrl)
{
    public string ToText() =>
        $"{UploaderName} added {Photos} photo(s) and {Videos} video(s) to \"{TabName}\" in {EventName}.\n\n" +
        $"Open: {Url}\nUnsubscribe: {UnsubscribeUrl}";
}

/// <summary>Add a new delivery channel by implementing this and registering it in DI. Nothing else changes.</summary>
internal interface INotificationChannel
{
    public NotifyChannel Channel { get; }
    public Task SendAsync(string address, NewContentMessage message, CancellationToken ct);
}

internal sealed class EmailChannel(IEmailSender sender) : INotificationChannel
{
    public NotifyChannel Channel => NotifyChannel.Email;
    public Task SendAsync(string address, NewContentMessage m, CancellationToken ct) =>
        sender.SendAsync(address, $"New in {m.EventName}", m.ToText(), ct);
}

/// <summary>Send-only for now. TODO: /start deep-link flow that stores chat ids as confirmed subscriptions.</summary>
internal sealed class TelegramChannel(IOptions<TelegramOptions> options) : INotificationChannel
{
    public NotifyChannel Channel => NotifyChannel.Telegram;

    public async Task SendAsync(string address, NewContentMessage m, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            return;
        }

        var bot = new TelegramBotClient(options.Value.BotToken);
        await bot.SendMessage(long.Parse(address), m.ToText(), cancellationToken: ct);
    }
}
