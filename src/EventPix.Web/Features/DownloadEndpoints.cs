using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

internal static class DownloadEndpoints
{
    private const int MaxSelectedItems = 500;

    internal sealed record DownloadSelectedDto(Guid[]? Ids);

    /// <summary>
    /// GET /download                -> whole library
    /// GET /download?tab={id}       -> one tab
    /// GET /download?starred=true   -> the caller's starred files (optionally combined with tab)
    /// POST /download {ids}         -> the given items of THIS event only (multi-select download)
    /// The ZIP is streamed straight to the response: no temp file, constant memory.
    /// Media is already compressed, so entries are stored (NoCompression).
    /// </summary>
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/download", async (Guid? tab, bool? starred, HttpContext h, EventPixDb db, IMediaStorage storage, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (!ev.DownloadsOpen && me is not { Role: MemberRole.Owner })
            {
                return Results.StatusCode(403);
            }

            var q = db.MediaItems.AsNoTracking().Where(m => m.EventId == ev.Id && m.Status == MediaStatus.Ready);
            if (tab is not null)
            {
                q = q.Where(m => m.TabId == tab);
            }

            if (starred == true)
            {
                if (me is null)
                {
                    return Results.Unauthorized();   // stars are per member: guests have none
                }

                q = q.Where(m => db.Stars.Any(s => s.MemberId == me.Id && s.MediaId == m.Id));
            }

            var items = await q.OrderBy(m => m.TabId).ThenBy(m => m.UploadedUtc).ToListAsync(ct);
            if (items.Count == 0)
            {
                return Results.NotFound();
            }

            var scopeName = starred == true ? "starred" : "all";
            if (tab is not null)
            {
                scopeName = await db.Tabs.Where(t => t.Id == tab && t.EventId == ev.Id)
                    .Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "tab";
            }

            return await WriteZipAsync(h, db, storage, ev, items, scopeName, ct);
        });

        g.MapPost("/download", async (DownloadSelectedDto dto, HttpContext h, EventPixDb db, IMediaStorage storage, CancellationToken ct) =>
        {
            if (dto.Ids is not { Length: > 0 })
            {
                return Results.BadRequest(new { error = "invalid_ids" });
            }

            if (dto.Ids.Length > MaxSelectedItems)
            {
                return Results.BadRequest(new { error = "too_many" });
            }

            var (ev, me) = h.GetScope();
            if (!ev.DownloadsOpen && me is not { Role: MemberRole.Owner })
            {
                return Results.StatusCode(403);
            }

            // Ids from other events (or unknown/processing ids) are silently dropped: no existence oracle.
            var items = await db.MediaItems.AsNoTracking()
                .Where(m => m.EventId == ev.Id && m.Status == MediaStatus.Ready && dto.Ids.Contains(m.Id))
                .OrderBy(m => m.TabId).ThenBy(m => m.UploadedUtc)
                .ToListAsync(ct);
            if (items.Count == 0)
            {
                return Results.NotFound();
            }

            return await WriteZipAsync(h, db, storage, ev, items, "selection", ct);
        });
    }

    private static async Task<IResult> WriteZipAsync(
        HttpContext h, EventPixDb db, IMediaStorage storage, Event ev, List<MediaItem> items, string scopeName, CancellationToken ct)
    {
        var tabNames = await db.Tabs.Where(t => t.EventId == ev.Id).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        h.Response.ContentType = "application/zip";
        h.Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment") { FileNameStar = $"{Safe(ev.Name)}-{Safe(scopeName)}.zip" }.ToString();

        // ZipArchive must stream to a non-seekable body, so entries use data descriptors, and the BCL
        // has no async path for those descriptor/central-directory writes during disposal (tiny payloads).
        // Opt this response into synchronous IO; the large entry copies below stay fully async.
        h.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

        // .NET 10 async ZIP APIs: required because Kestrel forbids synchronous writes to the response body.
        await using var zip = await ZipArchive.CreateAsync(h.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in items)
        {
            var name = Unique(used, $"{Safe(tabNames.GetValueOrDefault(m.TabId, "tab"))}/{Safe(m.OriginalFileName)}");
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            await using var dst = await entry.OpenAsync(ct);
            await using var src = storage.OpenRead(m.StorageKey + "/original");
            await src.CopyToAsync(dst, ct);
        }

        return Results.Empty;
    }

    internal static string Safe(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c);
        }

        var r = sb.ToString().Trim().Trim('.');
        return r.Length == 0 ? "file" : r.Length > 120 ? r[..120] : r;
    }

    internal static string Unique(HashSet<string> used, string path)
    {
        var candidate = path;
        var dot = path.LastIndexOf('.');
        var stem = dot > path.LastIndexOf('/') ? path[..dot] : path;
        var ext = dot > path.LastIndexOf('/') ? path[dot..] : "";
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = $"{stem} ({i}){ext}";
        }

        return candidate;
    }
}
