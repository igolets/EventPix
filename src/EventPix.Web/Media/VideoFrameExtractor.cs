using System.Diagnostics;
using EventPix.Web.Configuration;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Media;

/// <summary>Runs ffmpeg as an external process (never linked, so its licence does not affect ours).</summary>
internal sealed class VideoFrameExtractor(IOptions<EventPixOptions> options, ILogger<VideoFrameExtractor> log)
{
    public async Task<byte[]?> ExtractAsync(string inputPath, CancellationToken ct)
    {
        var ffmpeg = options.Value.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            return null;
        }

        foreach (var seek in new[] { "1", "0" })   // 1s in; fall back to first frame for very short clips
        {
            var outPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");
            try
            {
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", seek, "-i", inputPath,
                                          "-frames:v", "1", "-vf", "scale='min(1920,iw)':-2", outPath })
                {
                    psi.ArgumentList.Add(a);          // ArgumentList: no shell, no injection
                }

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    return null;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(2));
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(true); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    throw;
                }

                if (File.Exists(outPath))
                {
                    return await File.ReadAllBytesAsync(outPath, ct);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                log.FfmpegMissing(e, ffmpeg);
                return null;
            }
            finally
            {
                if (File.Exists(outPath))
                {
                    File.Delete(outPath);
                }
            }
        }
        return null;
    }
}
