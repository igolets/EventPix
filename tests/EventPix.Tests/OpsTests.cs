using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EventPix.Web.Data;
using EventPix.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventPix.Tests;

public sealed class OpsTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose()
    {
        _app.Dispose();
    }

    [Fact]
    public async Task Healthz_Reports_Database_Readiness_And_Nothing_Else()
    {
        var client = _app.CreateClient();

        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_Under_The_Event_Quota_Is_Accepted()
    {
        var owner = OwnerEmail();
        var (token, _) = await _app.CreateEventAsync(owner);
        using var scoped = _app.Factory.WithWebHostBuilder(b => b.UseSetting("EventPix:EventQuotaBytes", "1048576"));
        var client = scoped.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "eventpix");
        await _app.SignInAsync(client, token, owner);

        var response = await UploadAsync(client, token, "a.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<UploadBatch>();
        var entry = Assert.Single(batch!.Results);
        Assert.Null(entry.Error);
        Assert.NotNull(entry.Id);
    }

    [Fact]
    public async Task Upload_Beyond_The_Event_Quota_Is_Rejected_Without_Storing()
    {
        var owner = OwnerEmail();
        var (token, eventId) = await _app.CreateEventAsync(owner);
        using var scoped = _app.Factory.WithWebHostBuilder(b => b.UseSetting("EventPix:EventQuotaBytes", "1"));
        var client = scoped.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "eventpix");
        await _app.SignInAsync(client, token, owner);

        var response = await UploadAsync(client, token, "big.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<UploadBatch>();
        var entry = Assert.Single(batch!.Results);
        Assert.Equal("quota_exceeded", entry.Error);
        using var scope = scoped.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.MediaItems.Where(m => m.EventId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Rejected_Scan_Stores_Nothing_And_Reports_The_File()
    {
        var scanner = new RejectingScanner();
        using var app = new TestApp(s => s.AddSingleton<IAntivirusScanner>(scanner));
        var owner = OwnerEmail();
        var (token, eventId) = await app.CreateEventAsync(owner);
        var client = app.CreateClient();
        await app.SignInAsync(client, token, owner);

        var response = await UploadAsync(client, token, "eicar.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<UploadBatch>();
        var entry = Assert.Single(batch!.Results);
        Assert.Equal("rejected_by_scanner", entry.Error);
        Assert.Equal(1, scanner.Calls);
        using var scope = app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.MediaItems.Where(m => m.EventId == eventId).ToListAsync());
    }

    private static string OwnerEmail()
    {
        return "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, string name)
    {
        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(TestJpeg.Asymmetric());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "files", name);
        return await client.PostAsync(new Uri("/api/e/" + token + "/uploads", UriKind.Relative), content);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record UploadBatch(Guid TabId, IReadOnlyList<UploadEntry> Results);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1812", Justification = "Instantiated by the JSON serializer.")]
    internal sealed record UploadEntry(string Name, Guid? Id, string? Error);

    private sealed class RejectingScanner : IAntivirusScanner
    {
        public int Calls;

        public Task ScanAsync(Stream content, string fileName, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("test signature detected");
        }
    }
}
