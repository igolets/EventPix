using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace EventPix.Tests;

public sealed class SubscriptionTests : IDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Subscriptions_Crud_And_Unlinked_Channel()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        // Telegram has no deep-link flow yet: the API refuses it explicitly.
        Assert.Equal(HttpStatusCode.NotImplemented, (await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Telegram", TabId = (Guid?)null, Mode = "Instant" })).StatusCode);

        var put = await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Instant" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var id = (await put.Content.ReadFromJsonAsync<IdDto>())!.Id;

        // Second PUT updates the same subscription instead of creating a duplicate.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Digest" })).StatusCode);

        var list = await client.GetFromJsonAsync<SubDto[]>(U($"/api/e/{token}/subscriptions"), _json);
        var sub = Assert.Single(list!);
        Assert.Equal(id, sub.Id);
        Assert.Equal("Digest", sub.Mode);
        Assert.True(sub.Confirmed);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U($"/api/e/{token}/subscriptions/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(U($"/api/e/{token}/subscriptions/{id}"))).StatusCode);
        var after = await client.GetFromJsonAsync<SubDto[]>(U($"/api/e/{token}/subscriptions"), _json);
        Assert.Empty(after!);
    }

    [Fact]
    public async Task Unknown_Tab_Subscription_Is_Not_Found()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = Guid.NewGuid(), Mode = "Instant" })).StatusCode);
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record IdDto(Guid Id);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record SubDto(Guid Id, string Channel, Guid? TabId, string Mode, bool Confirmed);
}
