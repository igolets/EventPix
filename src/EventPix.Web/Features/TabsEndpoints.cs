using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

internal sealed record TabDto(Guid Id, string Name, TabKind Kind, int Count, bool CanUpload, bool Mine, bool CanRename);
internal sealed record CreateTabDto(string? Name, bool AllowGuestUploads, bool Personal);
internal sealed record PatchTabDto(string? Name, bool? AllowGuestUploads);
internal sealed record ReorderTabsDto(Guid[]? Ids);

internal static class TabsEndpoints
{
    private static readonly string[] _blobSuffixes = ["/original", "/thumb.jpg", "/preview.jpg"];

    public static bool CanUploadTo(Event ev, Member? me, Tab tab)
    {
        return me is not null &&
        (me.Role == MemberRole.Owner ||
        (ev.UploadsOpen && (tab.Kind == TabKind.Author ? tab.OwnerMemberId == me.Id : tab.AllowGuestUploads)));
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/tabs", async (HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var tabs = await db.Tabs.AsNoTracking().Where(t => t.EventId == ev.Id)
                .OrderBy(t => t.Kind).ThenBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
            var counts = await db.MediaItems.Where(m => m.EventId == ev.Id && m.Status == MediaStatus.Ready)
                .GroupBy(m => m.TabId).Select(x => new { x.Key, N = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return Results.Ok(tabs.Select(t => new TabDto(
                t.Id, t.Name, t.Kind, counts.GetValueOrDefault(t.Id), CanUploadTo(ev, me, t),
                me is not null && t.OwnerMemberId == me.Id,
                me is { Role: MemberRole.Owner } || (me is not null && t.OwnerMemberId == me.Id))));
        });

        g.MapPost("/tabs", async (CreateTabDto dto, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var name = dto.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                return Results.BadRequest(new { error = "invalid_name" });
            }

            var next = (await db.Tabs.Where(t => t.EventId == ev.Id).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 1;
            var personal = dto.Personal || me!.Role != MemberRole.Owner;   // guests always create their own tabs
            var tab = personal
                ? new Tab { EventId = ev.Id, Name = name, Kind = TabKind.Author, OwnerMemberId = me!.Id, SortOrder = next }
                : new Tab { EventId = ev.Id, Name = name, Kind = TabKind.Section, SortOrder = next, AllowGuestUploads = dto.AllowGuestUploads };
            db.Tabs.Add(tab);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)   // the member already has an author tab with this name
            {
                return Results.Json(new { error = "name_in_use" }, statusCode: 409);
            }

            return Results.Created($"tabs/{tab.Id}", new { tab.Id });
        }).RequireMember();

        g.MapPatch("/tabs/{id:guid}", async (Guid id, PatchTabDto dto, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var tab = await db.Tabs.FirstOrDefaultAsync(t => t.Id == id && t.EventId == ev.Id, ct);
            if (tab is null)
            {
                return Results.NotFound();
            }

            var isOwner = me!.Role == MemberRole.Owner;
            if (!isOwner && tab.OwnerMemberId != me.Id)   // a member may rename their own author tabs only
            {
                return Results.StatusCode(403);
            }

            if (dto.Name is not null)
            {
                var name = dto.Name.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 100)
                {
                    return Results.BadRequest(new { error = "invalid_name" });
                }

                tab.Name = name;
            }

            if (dto.AllowGuestUploads is { } open && tab.Kind == TabKind.Section && isOwner)
            {
                tab.AllowGuestUploads = open;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)   // the member already has an author tab with this name
            {
                return Results.Json(new { error = "name_in_use" }, statusCode: 409);
            }

            return Results.NoContent();
        }).RequireMember();

        g.MapPut("/tabs/order", async (ReorderTabsDto dto, HttpContext h, EventPixDb db, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (me!.Role != MemberRole.Owner)
            {
                return Results.StatusCode(403);
            }

            var ids = dto.Ids ?? [];
            if (ids.Length == 0 || ids.Distinct().Count() != ids.Length)
            {
                return Results.BadRequest(new { error = "invalid_order" });
            }

            var tabs = await db.Tabs.Where(t => t.EventId == ev.Id && t.Kind == TabKind.Section).ToListAsync(ct);
            if (ids.Any(i => tabs.All(t => t.Id != i)))
            {
                return Results.NotFound();
            }

            var order = 0;
            foreach (var tab in ids.Select(id => tabs.First(t => t.Id == id)))
            {
                tab.SortOrder = ++order;
            }

            foreach (var tab in tabs.Where(t => !ids.Contains(t.Id)).OrderBy(t => t.SortOrder))
            {
                tab.SortOrder = ++order;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireMember();

        g.MapDelete("/tabs/{id:guid}", async (Guid id, HttpContext h, EventPixDb db, IMediaStorage storage, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            var tab = await db.Tabs.FirstOrDefaultAsync(t => t.Id == id && t.EventId == ev.Id, ct);
            if (tab is null)
            {
                return Results.NotFound();
            }

            if (me!.Role != MemberRole.Owner && (tab.Kind != TabKind.Author || tab.OwnerMemberId != me.Id))
            {
                return Results.StatusCode(403);   // members may delete only their own author tabs
            }

            var keys = await db.MediaItems.Where(m => m.TabId == id && m.EventId == ev.Id)
                .Select(m => m.StorageKey).ToListAsync(ct);
            db.Tabs.Remove(tab);            // cascades to media items and stars
            await db.SaveChangesAsync(ct);
            foreach (var key in keys)
            {
                foreach (var suffix in _blobSuffixes)
                {
                    await storage.DeleteAsync(key + suffix, ct);
                }
            }

            return Results.NoContent();
        }).RequireMember();
    }
}
