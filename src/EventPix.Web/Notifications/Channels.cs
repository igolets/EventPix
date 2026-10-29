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

/// <summary>
/// Deliver one already-composed message to one address. The worker composes every text (Composer);
/// a channel only knows how to transport a subject + body. New channel = one class + one DI line.
/// </summary>
internal interface INotificationChannel
{
    public NotifyChannel Channel { get; }
    public Task SendAsync(string address, string subject, string body, CancellationToken ct);
}

internal sealed class EmailChannel(IEmailSender sender) : INotificationChannel
{
    public NotifyChannel Channel => NotifyChannel.Email;

    public Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        return sender.SendAsync(address, subject, body, ct);
    }
}

internal sealed class TelegramChannel(IOptions<TelegramOptions> options) : INotificationChannel
{
    public NotifyChannel Channel => NotifyChannel.Telegram;

    public async Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            return;
        }

        var bot = new TelegramBotClient(options.Value.BotToken);
        await bot.SendMessage(long.Parse(address), body, cancellationToken: ct);
    }
}

/// <summary>MAX delivery rides the same gateway the link worker polls, so tokens are checked in one place.</summary>
internal sealed class MaxChannel(IEnumerable<IBotLinkGateway> gateways) : INotificationChannel
{
    private readonly IBotLinkGateway _gateway = gateways.LastOrDefault(g => g.Channel == NotifyChannel.Max)
        ?? throw new InvalidOperationException("No MAX bot gateway is registered.");

    public NotifyChannel Channel => NotifyChannel.Max;

    public Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        return _gateway.SendAsync(long.Parse(address), body, ct);
    }
}
