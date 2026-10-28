using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Notifications;

/// <summary>
/// MAX messenger bot gateway over the plain REST API (https://dev.max.ru/docs-api, base
/// platform-api2.max.ru, raw token in the Authorization header). Long polls GET /updates for
/// deep-link starts (bot_started carries the payload) and /start text messages; sends via
/// POST /messages. No official .NET SDK exists; the BCL HttpClient is preferred over a package.
/// </summary>
internal sealed class MaxBotGateway(IOptions<MaxOptions> options) : IBotLinkGateway
{
    private const string BaseUri = "https://platform-api2.max.ru/";

    // One client for the app lifetime: the long poll (timeout=30s) needs a generous client timeout.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public NotifyChannel Channel => NotifyChannel.Max;

    public async Task<string?> GetBotUsernameAsync(CancellationToken ct)
    {
        using var response = await RequestAsync(HttpMethod.Get, "me", ct);
        return (await ReadAsync<BotInfo>(response, ct))?.Username;
    }

    public async Task SendAsync(long chatId, string text, CancellationToken ct)
    {
        if (Token is null)
        {
            return;
        }

        var payload = new StringContent(JsonSerializer.Serialize(new MaxText(text), _json), Encoding.UTF8,
            new MediaTypeHeaderValue("application/json"));
        using var response = await RequestAsync(HttpMethod.Post, $"messages?chat_id={chatId}", ct, payload);
    }

    public string BuildLinkUrl(string botName, string code)
    {
        return $"https://max.ru/{botName}?start={code}";
    }

    public async Task ReceiveAsync(Func<long, string?, CancellationToken, Task> onMessage, CancellationToken ct)
    {
        if (Token is null)
        {
            return;
        }

        // Without a marker the API returns only the latest update; after the first response we
        // replay its marker to receive everything in order. Markers are not persisted across
        // restarts (same as the Telegram worker's offset), so at most the newest update replays.
        long? marker = null;
        while (!ct.IsCancellationRequested)
        {
            var page = await PollAsync(onMessage, marker, ct);
            marker = page?.Marker ?? marker;
        }
    }

    private async Task<Updates?> PollAsync(
        Func<long, string?, CancellationToken, Task> onMessage, long? marker, CancellationToken ct)
    {
        var query = "updates?types=bot_started&types=message_created&timeout=30";
        if (marker is not null)
        {
            query += $"&marker={marker}";
        }

        Updates? page;
        using (var response = await RequestAsync(HttpMethod.Get, query, ct))
        {
            page = await ReadAsync<Updates>(response, ct);
        }

        foreach (var update in page?.Items ?? [])
        {
            var (chatId, text) = ToMessage(update);
            if (chatId != 0)
            {
                await onMessage(chatId, text, ct);
            }
        }

        return page;
    }

    /// <summary>Normalises updates to the same (chat, "/start …") shape the Telegram gateway yields.
    /// Internal for tests: this is the deep-link payload ingestion path.</summary>
    internal static (long ChatId, string? Text) ToMessage(Update update)
    {
        return update.UpdateType switch
        {
            "bot_started" => (update.ChatId ?? 0, "/start " + update.Payload),
            "message_created" => (update.Message?.Recipient?.ChatId ?? update.ChatId ?? 0,
                update.Message?.Body?.Text ?? update.Message?.Text),
            _ => (0, null),
        };
    }

    private string? Token => string.IsNullOrWhiteSpace(options.Value.BotToken) ? null : options.Value.BotToken;

    private async Task<HttpResponseMessage> RequestAsync(
        HttpMethod method, string uri, CancellationToken ct, HttpContent? content = null)
    {
        // MAX authenticates with the raw access token (no Bearer prefix). Never logged.
        var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUri), uri)) { Content = content };
        try
        {
            request.Headers.TryAddWithoutValidation("Authorization", Token ?? "");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return response;
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct);
    }

    private sealed record MaxText([property: JsonPropertyName("text")] string Text);

    internal sealed record Updates(
        [property: JsonPropertyName("updates")] List<Update>? Items,
        [property: JsonPropertyName("marker")] long? Marker);

    internal sealed record Update(
        [property: JsonPropertyName("update_type")] string? UpdateType,
        [property: JsonPropertyName("chat_id")] long? ChatId,
        [property: JsonPropertyName("payload")] string? Payload,
        [property: JsonPropertyName("message")] MaxMessage? Message);

    internal sealed record MaxMessage(
        [property: JsonPropertyName("recipient")] Recipient? Recipient,
        [property: JsonPropertyName("body")] MaxBody? Body,
        [property: JsonPropertyName("text")] string? Text);

    internal sealed record Recipient([property: JsonPropertyName("chat_id")] long? ChatId);

    internal sealed record MaxBody([property: JsonPropertyName("text")] string? Text);

    internal sealed record BotInfo([property: JsonPropertyName("username")] string? Username);
}
