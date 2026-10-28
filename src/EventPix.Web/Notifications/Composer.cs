using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventPix.Web.Notifications;

/// <summary>One (tab, uploader) batch of new media. A digest message lists several.</summary>
internal sealed record ContentGroup(string Tab, string Uploader, int Photos, int Videos);

/// <summary>Turns outbox payloads into the plain texts every channel delivers. Channels never format data.</summary>
internal static class Composer
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IReadOnlyList<ContentGroup> groups)
    {
        return JsonSerializer.Serialize(groups, _json);
    }

    public static List<ContentGroup> Deserialize(string json)
    {
        var items = JsonSerializer.Deserialize<ContentGroup[]>(json, _json);
        return items is null ? [] : [.. items];
    }

    public static string Subject(string eventName, IReadOnlyList<ContentGroup> groups)
    {
        var single = $"New in {eventName}";
        var many = $"New in {eventName} ({groups.Count} updates)";
        return groups.Count == 1 ? single : many;
    }

    public static string Body(IReadOnlyList<ContentGroup> groups, string url, string unsubscribeUrl)
    {
        var lines = groups.Select(g =>
            $"{g.Uploader} added {g.Photos} photo(s) and {g.Videos} video(s) to \"{g.Tab}\".");
        return string.Join('\n', lines) + $"\n\nOpen: {url}\n\nUnsubscribe: {unsubscribeUrl}";
    }
}
