using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// M0 smoke test: full stack against an in-memory host with a temp SQLite DB, pepper and storage dir.
/// Flow: create event -> request code -> verify -> create tab -> list tabs.
/// </summary>
public sealed class AppSmokeTests : IDisposable
{
    private readonly SmokeFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CreateEvent_RequestCode_Verify_ListTabs()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "eventpix");

        var email = $"smoke-{Guid.NewGuid():N}@example.com";
        var token = await CreateEventAsync(email);

        var requested = await client.PostAsJsonAsync($"/api/e/{token}/auth/request", new { Email = email });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        var code = await WaitForCodeAsync(email);
        var verified = await client.PostAsJsonAsync($"/api/e/{token}/auth/verify", new { Email = email, Code = code });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        var me = await client.GetAsync(new Uri($"/api/e/{token}/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var tabName = "Ceremony " + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync($"/api/e/{token}/tabs", new { Name = tabName, AllowGuestUploads = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The API serialises enums as strings (JsonStringEnumConverter); mirror that here.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var tabs = await client.GetFromJsonAsync<EventPix.Web.Features.TabDto[]>(new Uri($"/api/e/{token}/tabs", UriKind.Relative), json);
        Assert.NotNull(tabs);
        Assert.Contains(tabs, t => t.Name == tabName);
    }

    /// <summary>Same write path as the create-event CLI command.</summary>
    private async Task<string> CreateEventAsync(string ownerEmail)
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var tokens = sp.GetRequiredService<EventTokenService>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        var (token, hash, prot) = tokens.Create();
        var ev = new Event { Name = "Smoke test event", OwnerEmail = ownerEmail, TokenHash = hash, TokenProtected = prot, CreatedUtc = now };
        db.Events.Add(ev);
        db.Members.Add(new Member { EventId = ev.Id, Email = ownerEmail, DisplayName = "smoke", Role = MemberRole.Owner, CreatedUtc = now });
        await db.SaveChangesAsync();
        return token;
    }

    /// <summary>Dev mail (LogEmailSender) prints the OTP to the log; capture it from there.</summary>
    private async Task<string> WaitForCodeAsync(string email)
    {
        var rx = new Regex(@"Your code: (\d{6})");
        for (var i = 0; i < 50; i++)
        {
            var hit = _factory.Logs.Lines.Select(l => rx.Match(l)).FirstOrDefault(m => m.Success);
            if (hit is not null)
            {
                return hit.Groups[1].Value;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"No OTP logged for {email}.");
    }

    private sealed class SmokeFactory : WebApplicationFactory<Program>
    {
        public CapturingLoggerProvider Logs { get; } = new();

        private string Root { get; } = Path.Combine(Path.GetTempPath(), "eventpix-smoke-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Path.Combine(Root, "media"));

            // Theme folders stay in the repo; find them by walking up from the test bin directory.
            var dir = AppContext.BaseDirectory;
            string? themes = null;
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir, "themes", "default", "index.html")))
                {
                    themes = Path.Combine(dir, "themes");
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(themes);

            builder.UseSetting($"{EventPixOptions.Section}:DataPath", Path.Combine(Root, "data"));
            builder.UseSetting($"{EventPixOptions.Section}:StoragePath", Path.Combine(Root, "media"));
            builder.UseSetting($"{EventPixOptions.Section}:ThemesPath", themes);
            builder.UseSetting($"{SmtpOptions.Section}:Host", "");   // LogEmailSender prints codes to the log
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose() { }

        private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                {
                    owner.Lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
