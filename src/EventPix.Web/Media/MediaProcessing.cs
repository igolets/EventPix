using System.Threading.Channels;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Media;

internal sealed class MediaQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid mediaId)
    {
        return _channel.Writer.WriteAsync(mediaId);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}

/// <summary>Generates thumb/preview variants after upload. Resumes unfinished items on start-up.</summary>
internal sealed class MediaProcessingWorker(
    MediaQueue queue, IServiceScopeFactory scopes, ILogger<MediaProcessingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var pending = await db.MediaItems.Where(m => m.Status == MediaStatus.Processing)
                .Select(m => m.Id).ToListAsync(ct);
            foreach (var id in pending)
            {
                await queue.EnqueueAsync(id);
            }
        }

        await foreach (var id in queue.ReadAllAsync(ct))
        {
            try { await ProcessAsync(id, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.ProcessFailed(e, id);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
                await db.MediaItems.Where(m => m.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MediaStatus.Failed), ct);
            }
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        var video = scope.ServiceProvider.GetRequiredService<VideoFrameExtractor>();
        var probe = scope.ServiceProvider.GetRequiredService<VideoProbe>();

        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null || item.Status != MediaStatus.Processing)
        {
            return;
        }

        if (item.Kind == MediaKind.Photo)
        {
            await using var src = storage.OpenRead(item.StorageKey + "/original");
            var v = ImageVariants.Create(src) ?? throw new InvalidDataException("Unreadable image.");
            await storage.SaveAsync(item.StorageKey + "/thumb.jpg", new MemoryStream(v.Thumb), ct);
            await storage.SaveAsync(item.StorageKey + "/preview.jpg", new MemoryStream(v.Preview), ct);
            item.Width = v.Width; item.Height = v.Height;

            src.Position = 0;
            item.TakenUtc = ExifReader.ReadTakenUtc(src)?.UtcDateTime;   // photos only: EXIF lives in JPEGs
        }
        else
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                await using (var src = storage.OpenRead(item.StorageKey + "/original"))
                await using (var dst = File.Create(tmp))
                {
                    await src.CopyToAsync(dst, ct);
                }

                var frame = await video.ExtractAsync(tmp, ct);
                if (frame is not null)
                {
                    var v = ImageVariants.Create(new MemoryStream(frame));
                    if (v is { } r)
                    {
                        await storage.SaveAsync(item.StorageKey + "/thumb.jpg", new MemoryStream(r.Thumb), ct);
                        await storage.SaveAsync(item.StorageKey + "/preview.jpg", new MemoryStream(r.Preview), ct);
                        item.Width = r.Width; item.Height = r.Height;
                    }
                }

                var info = await probe.ProbeAsync(tmp, ct);   // duration + capture time from container metadata
                if (info is { } vi)
                {
                    item.DurationSeconds = vi.Duration?.TotalSeconds;
                    item.TakenUtc = vi.CreatedUtc?.UtcDateTime;
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
        }

        item.Status = MediaStatus.Ready;
        await db.SaveChangesAsync(ct);
    }
}
