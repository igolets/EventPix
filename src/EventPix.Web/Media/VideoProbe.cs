using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EventPix.Web.Configuration;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Media;

internal readonly record struct VideoInfo(TimeSpan? Duration, DateTimeOffset? CreatedUtc);

/// <summary>Runs ffprobe as an external process (like ffmpeg: never linked, invoked via ArgumentList only).</summary>
internal sealed class VideoProbe(IOptions<EventPixOptions> options, ILogger<VideoProbe> log)
{
    public async Task<VideoInfo?> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var ffprobe = options.Value.FfprobePath;
        if (string.IsNullOrWhiteSpace(ffprobe))
        {
            return null;
        }

        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-print_format", "json", "-show_format", inputPath })
        {
            psi.ArgumentList.Add(a);          // ArgumentList: no shell, no injection
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return Parse(stdout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.ProbeTimeout(inputPath);
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.FfprobeFailed(e, ffprobe);
            return null;
        }
    }

    /// <summary>Parses ffprobe's JSON output: format.duration and format.tags.creation_time. Tolerates absence of both.</summary>
    internal static VideoInfo Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var duration = format.TryGetProperty("duration", out var d) ? Seconds(d.GetString()) : null;
            DateTimeOffset? created = null;
            if (format.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
                tags.TryGetProperty("creation_time", out var c))
            {
                created = ParseTimestamp(c.GetString());
            }

            return new VideoInfo(duration, created);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    internal static TimeSpan? Seconds(string? value) =>
        double.TryParse(value, CultureInfo.InvariantCulture, out var s) && s is >= 0 and < double.MaxValue
            ? TimeSpan.FromSeconds(s)
            : null;

    /// <summary>ffprobe emits ISO 8601 timestamps, usually already UTC.</summary>
    internal static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
