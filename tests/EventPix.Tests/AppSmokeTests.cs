using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPix.Web.Features;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// M0 smoke test: full stack against an in-memory host with a temp SQLite DB, pepper and storage dir.
/// Flow: create event -> request code -> verify -> create tab -> list tabs -> profile -> sign-out.
/// </summary>
public sealed class AppSmokeTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose()
    {
        _app.Dispose();
    }

    [Fact]
    public async Task CreateEvent_RequestCode_Verify_ListTabs()
    {
        var client = _app.CreateClient();

        var email = $"smoke-{Guid.NewGuid():N}@example.com";
        var (token, _) = await _app.CreateEventAsync(email);
        await _app.SignInAsync(client, token, email);

        var meResponse = await client.GetAsync(U($"/api/e/{token}/me"));
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var tabName = "Ceremony " + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync(U($"/api/e/{token}/tabs"), new { Name = tabName, AllowGuestUploads = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The API serialises enums as strings (JsonStringEnumConverter); mirror that here.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var tabs = await client.GetFromJsonAsync<TabDto[]>(U($"/api/e/{token}/tabs"), json);
        Assert.NotNull(tabs);
        Assert.Contains(tabs, t => t.Name == tabName);

        // Profile update, then sign-out revokes the session.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync(U($"/api/e/{token}/me"), new { DisplayName = "Renamed" })).StatusCode);
        var me = await client.GetFromJsonAsync<MeDto>(U($"/api/e/{token}/me"), json);
        Assert.Equal("Renamed", me!.DisplayName);
        Assert.True(me.IsOwner);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(U($"/api/e/{token}/auth/logout"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(U($"/api/e/{token}/me"))).StatusCode);
    }

    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record MeDto(string DisplayName, bool IsOwner);
}
