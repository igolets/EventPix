using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Configuration;
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
    public async Task Subscriptions_Crud_Email_And_Telegram_Needs_The_Link_Flow()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var put = await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Instant" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var id = (await put.Content.ReadFromJsonAsync<IdDto>())!.Id;

        // Second PUT updates the same subscription instead of duplicating.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Digest" })).StatusCode);

        // Telegram chat ids can only be proven by the bot: the API refuses and points at the link flow.
        var telegram = await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Telegram", TabId = (Guid?)null, Mode = "Instant" });
        Assert.Equal(HttpStatusCode.BadRequest, telegram.StatusCode);
        var err = await telegram.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("telegram_use_link", err!.Error);

        // No bot token configured: the deep-link endpoint says so without leaking anything.
        Assert.Equal(HttpStatusCode.NotImplemented,
            (await client.GetAsync(U($"/api/e/{token}/subscriptions/telegram-link"))).StatusCode);

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
    public async Task Per_Tab_Subscription_Is_Separate_From_The_All_Tabs_One()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);
        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);

        var tabResponse = await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = "Ceremony" });
        Assert.Equal(HttpStatusCode.Created, tabResponse.StatusCode);
        var tabId = (await tabResponse.Content.ReadFromJsonAsync<IdDto>())!.Id;

        await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Instant" });
        await client.PutAsJsonAsync(U($"/api/e/{token}/subscriptions"),
            new { Channel = "Email", TabId = tabId, Mode = "Digest" });

        var list = await client.GetFromJsonAsync<SubDto[]>(U($"/api/e/{token}/subscriptions"), _json);
        Assert.Equal(2, list!.Length);
        Assert.Contains(list, s => s.TabId == tabId && s.Mode == "Digest");
        Assert.Contains(list, s => s.TabId == null && s.Mode == "Instant");
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

    [Fact]
    public async Task Public_Reports_Whether_The_Telegram_Bot_Is_Configured()
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(owner);

        // Default host has no bot token: the UI hides the Telegram channel.
        var off = await _app.CreateClient().GetFromJsonAsync<PublicDto>(U($"/api/e/{token}/public"));
        Assert.False(off!.Telegram);

        using var configured = _app.Factory.WithWebHostBuilder(
            b => b.UseSetting($"{TelegramOptions.Section}:BotToken", "test-token"));
        var on = await configured.CreateClient().GetFromJsonAsync<PublicDto>(U($"/api/e/{token}/public"));
        Assert.True(on!.Telegram);
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record PublicDto(string Name, string Description, bool InviteOnly, bool Telegram);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record IdDto(Guid Id);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record SubDto(Guid Id, string Channel, Guid? TabId, string Mode, bool Confirmed);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record ErrorDto(string Error);
}
