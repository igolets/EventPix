using EventPix.Web.Domain;

namespace EventPix.Web.Notifications;

internal static partial class Log
{
    [LoggerMessage(LogLevel.Warning, "DEV MAIL to {To}: {Subject}\n{Body}")]
    internal static partial void DevMail(this ILogger logger, string to, string subject, string body);

    [LoggerMessage(LogLevel.Error, "Notification tick failed")]
    internal static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Notification retry {Attempts} queued for {Channel} subscription {Id}")]
    internal static partial void RetryQueued(
        this ILogger logger, Exception exception, NotifyChannel channel, Guid id, int attempts);

    [LoggerMessage(LogLevel.Warning, "Notification dropped after {Attempts} attempts for {Channel} subscription {Id}")]
    internal static partial void Dropped(
        this ILogger logger, Exception exception, NotifyChannel channel, Guid id, int attempts);

    [LoggerMessage(LogLevel.Error, "Telegram polling failed")]
    internal static partial void PollingFailed(this ILogger logger, Exception exception);
}
