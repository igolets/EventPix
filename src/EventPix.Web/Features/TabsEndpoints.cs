using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Features;

internal sealed record TabDto(Guid Id, string Name, TabKind Kind, int Count, bool CanUpload, bool Mine);
internal sealed record CreateTabDto(string? Name, bool AllowGuestUploads);

internal static class TabsEndpoints
{
    public static bool CanUploadTo(Event ev, Member me, Tab tab) =>
        me.Role == MemberRole.Owner ||
        (ev.UploadsOpen && (tab.Kind == TabKind.Author ? tab.OwnerMemberId == me.Id : tab.AllowGuestUploads));

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
                t.Id, t.Name, t.Kind, counts.GetValueOrDefault(t.Id), CanUploadTo(ev, me!, t), t.OwnerMemberId == me!.Id)));
        });

        g.MapPost("/tabs", async (CreateTabDto dto, HttpContext h, EventPixDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var (ev, me) = h.GetScope();
            if (me!.Role != MemberRole.Owner)
            {
                return Results.StatusCode(403);
            }

            var name = dto.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                return Results.BadRequest(new { error = "invalid_name" });
            }

            var next = (await db.Tabs.Where(t => t.EventId == ev.Id).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0) + 1;
            var tab = new Tab { EventId = ev.Id, Name = name, Kind = TabKind.Section, SortOrder = next, AllowGuestUploads = dto.AllowGuestUploads };
            db.Tabs.Add(tab);
            await db.SaveChangesAsync(ct);
            return Results.Created($"tabs/{tab.Id}", new { tab.Id });
        });
        // TODO: PATCH /tabs/{id} (rename, reorder, toggle uploads), DELETE /tabs/{id} (also delete blobs).
    }
}
