using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Features;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// M0 smoke test: full stack against an in-memory host with a temp SQLite DB, pepper and storage dir.
/// Flow: create event -> request code -> verify -> create tab -> list tabs.
/// </summary>
public sealed class AppSmokeTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task CreateEvent_RequestCode_Verify_ListTabs()
    {
        var client = _app.CreateClient();

        var email = $"smoke-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(email);
        await _app.SignInAsync(client, token, email);

        var me = await client.GetAsync(new Uri($"/api/e/{token}/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var tabName = "Ceremony " + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync($"/api/e/{token}/tabs", new { Name = tabName, AllowGuestUploads = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The API serialises enums as strings (JsonStringEnumConverter); mirror that here.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var tabs = await client.GetFromJsonAsync<TabDto[]>(new Uri($"/api/e/{token}/tabs", UriKind.Relative), json);
        Assert.NotNull(tabs);
        Assert.Contains(tabs, t => t.Name == tabName);
    }
}
