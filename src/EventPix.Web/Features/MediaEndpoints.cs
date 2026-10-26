using System.Security.Cryptography;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Media;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Features;

internal sealed record MediaDto(
    Guid Id, MediaKind Kind, MediaStatus Status, string Name, int? Width, int? Height, long Size, string Uploader,
    bool Starred, bool CanDelete);

internal static class MediaEndpoints
{
    private static readonly HashSet<string> _variants = ["thumb", "preview", "original"];

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/tabs/{tabId:guid}/media", ListAsync);
        g.MapGet("/media/{id:guid}/{variant}", FileAsync);
        g.MapPost("/uploads", UploadAsync).RequireMember();
        g.MapDelete("/media/{id:guid}", DeleteAsync).RequireMember();
        g.MapPut("/media/{id:guid}/star", (Guid id, HttpContext h, EventPixDb db, TimeProvider c, CancellationToken ct) => SetStar(id, true, h, db, c, ct)).RequireMember();
        g.MapDelete("/media/{id:guid}/star", (Guid id, HttpContext h, EventPixDb db, TimeProvider c, CancellationToken ct) => SetStar(id, false, h, db, c, ct)).RequireMember();
    }

    private static async Task<IResult> ListAsync(Guid tabId, int? skip, int? take, HttpContext h, EventPixDb db, CancellationToken ct)
    {
        var (ev, me) = h.GetScope();
        var meId = me?.Id ?? Guid.Empty;
        var isOwner = me is { Role: MemberRole.Owner };
        if (!await db.Tabs.AnyAsync(t => t.Id == tabId && t.EventId == ev.Id, ct))
        {
            return Results.NotFound();
        }

        var items = await db.MediaItems.AsNoTracking()
            .Where(m => m.TabId == tabId && m.EventId == ev.Id)
            .OrderBy(m => m.TakenUtc ?? m.UploadedUtc).ThenBy(m => m.Id)
            .Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 120, 1, 300))
            .Select(m => new MediaDto(m.Id, m.Kind, m.Status, m.OriginalFileName, m.Width, m.Height, m.SizeBytes,
                db.Members.Where(u => u.Id == m.UploaderId).Select(u => u.DisplayName).FirstOrDefault() ?? "",
                db.Stars.Any(s => s.MemberId == meId && s.MediaId == m.Id),
                m.UploaderId == meId || isOwner))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> FileAsync(Guid id, string variant, bool? download, HttpContext h,
        EventPixDb db, IMediaStorage storage, CancellationToken ct)
    {
        var ev = h.GetScope().Event;
        if (!_variants.Contains(variant))
        {
            return Results.NotFound();
        }

        var m = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.EventId == ev.Id, ct);
        if (m is null)
        {
            return Results.NotFound();
        }

        if (variant != "original" && m.Status != MediaStatus.Ready)
        {
            return Results.NotFound();
        }

        if (variant == "original" && download == true && !ev.DownloadsOpen && h.GetScope().Member is not { Role: MemberRole.Owner })
        {
            return Results.StatusCode(403);
        }

        var key = variant switch
        {
            "thumb" => m.StorageKey + "/thumb.jpg",
            "preview" => m.StorageKey + "/preview.jpg",
            _ => m.StorageKey + "/original",
        };
        if (!storage.Exists(key))
        {
            return Results.NotFound();
        }

        h.Response.Headers.CacheControl = "private, max-age=86400";
        var stream = storage.OpenRead(key);
        if (variant != "original")
        {
            return Results.Stream(stream, "image/jpeg");
        }

        return download == true
            ? Results.File(stream, m.ContentType, m.OriginalFileName, enableRangeProcessing: true)
            : Results.Stream(stream, m.ContentType, enableRangeProcessing: true);   // range support => seekable <video>
    }

    private static async Task<IResult> UploadAsync(Guid? tab, HttpContext h, EventPixDb db, IMediaStorage storage,
        MediaQueue queue, IAntivirusScanner antivirus, IOptions<EventPixOptions> options,
        ILogger<Program> log, TimeProvider clock, CancellationToken ct)
    {
        var (ev, meNoTrack) = h.GetScope();
        var me = await db.Members.FirstAsync(m => m.Id == meNoTrack!.Id, ct);
        if (!ev.UploadsOpen && me.Role != MemberRole.Owner)
        {
            return Results.StatusCode(403);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        Tab? target;
        if (tab is null)   // default: the member's own oldest tab, created on first upload
        {
            target = await db.Tabs.Where(t => t.EventId == ev.Id && t.Kind == TabKind.Author && t.OwnerMemberId == me.Id)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).FirstOrDefaultAsync(ct);
            if (target is null)
            {
                target = new Tab { EventId = ev.Id, Name = me.DisplayName, Kind = TabKind.Author, OwnerMemberId = me.Id, SortOrder = 1000 };
                db.Tabs.Add(target);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)   // a parallel upload won the race and created the tab first: reuse it
                {
                    db.Entry(target).State = EntityState.Detached;
                    target = await db.Tabs.Where(t => t.EventId == ev.Id && t.Kind == TabKind.Author && t.OwnerMemberId == me.Id)
                        .OrderBy(t => t.SortOrder).ThenBy(t => t.Id).FirstAsync(ct);
                }
            }
        }
        else
        {
            target = await db.Tabs.FirstOrDefaultAsync(t => t.Id == tab && t.EventId == ev.Id, ct);
            if (target is null)
            {
                return Results.NotFound();
            }

            if (!TabsEndpoints.CanUploadTo(ev, me, target))
            {
                return Results.StatusCode(403);
            }
        }

        var quota = options.Value.EventQuotaBytes;
        var used = quota > 0
            ? await db.MediaItems.Where(m => m.EventId == ev.Id).SumAsync(m => (long?)m.SizeBytes, ct) ?? 0L
            : 0L;

        var form = await h.Request.ReadFormAsync(ct);
        var results = new List<object>();
        foreach (var file in form.Files)
        {
            if (quota > 0 && used + file.Length > quota)
            {
                results.Add(new { name = file.FileName, error = "quota_exceeded" });
                continue;
            }

            await using var s = file.OpenReadStream();
            var head = new byte[32];
            var n = await s.ReadAtLeastAsync(head, 12, throwOnEndOfStream: false, ct);
            var sniff = FileSniffer.Detect(head.AsSpan(0, n));
            if (sniff is null) { results.Add(new { name = file.FileName, error = "unsupported_type" }); continue; }

            s.Position = 0;
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
            if (await db.MediaItems.AnyAsync(m => m.TabId == target.Id && m.Sha256 == sha, ct))
            { results.Add(new { name = file.FileName, error = "duplicate" }); continue; }

            // Fail-closed: a scanner error (or a verdict) means the file is not stored.
            try
            {
                s.Position = 0;
                await antivirus.ScanAsync(s, file.FileName, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.ScannerRejected(e, file.FileName);
                results.Add(new { name = file.FileName, error = "rejected_by_scanner" });
                continue;
            }

            var id = Guid.CreateVersion7();
            var key = $"{ev.Id:N}/{id:N}";
            s.Position = 0;
            await storage.SaveAsync(key + "/original", s, ct);

            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "file";
            }

            db.MediaItems.Add(new MediaItem
            {
                Id = id,
                EventId = ev.Id,
                TabId = target.Id,
                UploaderId = me.Id,
                Kind = sniff.Value.Kind,
                Status = MediaStatus.Processing,
                OriginalFileName = name.Length > 200 ? name[..200] : name,
                ContentType = sniff.Value.ContentType,
                SizeBytes = file.Length,
                Sha256 = sha,
                StorageKey = key,
                UploadedUtc = now,
            });
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync(id);
            used += file.Length;
            results.Add(new { name = file.FileName, id });
        }
        return Results.Ok(new { tabId = target.Id, results });
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext h, EventPixDb db, IMediaStorage storage, CancellationToken ct)
    {
        var (ev, me) = h.GetScope();
        var m = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == id && x.EventId == ev.Id, ct);
        if (m is null)
        {
            return Results.NotFound();
        }

        if (m.UploaderId != me!.Id && me.Role != MemberRole.Owner)
        {
            return Results.StatusCode(403);
        }

        db.MediaItems.Remove(m);
        await db.SaveChangesAsync(ct);
        foreach (var suffix in new[] { "/original", "/thumb.jpg", "/preview.jpg" })
        {
            await storage.DeleteAsync(m.StorageKey + suffix, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> SetStar(Guid id, bool on, HttpContext h, EventPixDb db, TimeProvider clock, CancellationToken ct)
    {
        var (ev, me) = h.GetScope();
        if (!await db.MediaItems.AnyAsync(m => m.Id == id && m.EventId == ev.Id, ct))
        {
            return Results.NotFound();
        }

        var existing = await db.Stars.FirstOrDefaultAsync(s => s.MemberId == me!.Id && s.MediaId == id, ct);
        if (on && existing is null)
        {
            db.Stars.Add(new Star { MemberId = me!.Id, MediaId = id, CreatedUtc = clock.GetUtcNow().UtcDateTime });
        }

        if (!on && existing is not null)
        {
            db.Stars.Remove(existing);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
