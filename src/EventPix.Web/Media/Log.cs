namespace EventPix.Web.Media;

internal static partial class Log
{
    [LoggerMessage(LogLevel.Error, "Processing media {Id} failed")]
    internal static partial void ProcessFailed(this ILogger logger, Exception exception, Guid id);

    [LoggerMessage(LogLevel.Warning, "ffmpeg not found at '{Path}'; video thumbnails disabled.")]
    internal static partial void FfmpegMissing(this ILogger logger, Exception exception, string path);

    [LoggerMessage(LogLevel.Warning, "ffprobe not found or failed at '{Path}'; video metadata disabled.")]
    internal static partial void FfprobeFailed(this ILogger logger, Exception exception, string path);

    [LoggerMessage(LogLevel.Warning, "ffprobe timed out for '{Path}'.")]
    internal static partial void ProbeTimeout(this ILogger logger, string path);

    [LoggerMessage(LogLevel.Warning, "Antivirus scan failed or rejected '{Name}'.")]
    internal static partial void ScannerRejected(this ILogger logger, Exception exception, string name);
}
