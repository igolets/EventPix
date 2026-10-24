namespace EventPix.Web.Media;

internal static partial class Log
{
    [LoggerMessage(LogLevel.Error, "Processing media {Id} failed")]
    internal static partial void ProcessFailed(this ILogger logger, Exception exception, Guid id);

    [LoggerMessage(LogLevel.Warning, "ffmpeg not found at '{Path}'; video thumbnails disabled.")]
    internal static partial void FfmpegMissing(this ILogger logger, Exception exception, string path);
}
