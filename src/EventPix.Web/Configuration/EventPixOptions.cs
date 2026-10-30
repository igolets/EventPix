namespace EventPix.Web.Configuration;

internal sealed class EventPixOptions
{
    public const string Section = "EventPix";

    /// <summary>SQLite DB, Data Protection keys, pepper. Back this up.</summary>
    public string DataPath { get; set; } = "data";
    /// <summary>Photos and videos. Must be OUTSIDE the web root and the code directory.</summary>
    public string StoragePath { get; set; } = "media";
    /// <summary>Folder containing theme folders. Relative paths resolve against the app directory.</summary>
    public string ThemesPath { get; set; } = "themes";
    public string ActiveTheme { get; set; } = "default";
    /// <summary>Language of the texts the server composes (OTP e-mail, notifications, bot replies).
    /// "en" (default) or "ru"; see EventPix.Web.Localization.Messages.</summary>
    public string Language { get; set; } = "en";
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>Per-event storage cap over originals, thumbnails and previews. 0 = unlimited.</summary>
    public long EventQuotaBytes { get; set; }
    public string? FfmpegPath { get; set; } = "ffmpeg";
    public string? FfprobePath { get; set; } = "ffprobe";
    public int OtpMinutes { get; set; } = 10;
    public int NotifyDebounceMinutes { get; set; } = 5;
    /// <summary>Digest subscriptions roll up activity into one message per window (rolling, UTC).</summary>
    public int NotifyDigestHours { get; set; } = 24;
    /// <summary>Honour X-Forwarded-*. Enable only when the app is reachable exclusively through your proxy.</summary>
    public bool TrustForwardedHeaders { get; set; }
}

internal sealed class SmtpOptions
{
    public const string Section = "Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "EventPix <no-reply@example.com>";
    public bool UseStartTls { get; set; } = true;
}

internal sealed class TelegramOptions
{
    public const string Section = "Telegram";
    public string BotToken { get; set; } = "";
}

internal sealed class MaxOptions
{
    public const string Section = "Max";
    public string BotToken { get; set; } = "";
}
