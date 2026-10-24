using EventPix.Web.Domain;

namespace EventPix.Web.Notifications;

internal static partial class Log
{
    [LoggerMessage(LogLevel.Warning, "DEV MAIL to {To}: {Subject}\n{Body}")]
    internal static partial void DevMail(this ILogger logger, string to, string subject, string body);

    [LoggerMessage(LogLevel.Error, "Notification tick failed")]
    internal static partial void TickFailed(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Notify {Channel} failed for subscription {Id}")]
    internal static partial void NotifyFailed(this ILogger logger, Exception exception, NotifyChannel channel, Guid id);
}
