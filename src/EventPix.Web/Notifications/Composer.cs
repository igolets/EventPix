using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Localization;

namespace EventPix.Web.Notifications;

/// <summary>One (tab, uploader) batch of new media. A digest message lists several.</summary>
internal sealed record ContentGroup(string Tab, string Uploader, int Photos, int Videos);

/// <summary>
/// Turns outbox payloads into the plain texts every channel delivers. Channels never format data;
/// the words come from <see cref="Messages"/> in the configured language.
/// </summary>
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

    public static string Subject(MessageLanguage lang, string eventName, IReadOnlyList<ContentGroup> groups)
    {
        return Messages.NotifySubject(lang, eventName, groups.Count);
    }

    public static string Body(
        MessageLanguage lang, IReadOnlyList<ContentGroup> groups, string url, string unsubscribeUrl)
    {
        var lines = groups.Select(g => Messages.NotifyGroupLine(lang, g.Uploader, g.Tab, g.Photos, g.Videos));
        return string.Join('\n', lines) + Messages.NotifyFooter(lang, url, unsubscribeUrl);
    }
}
