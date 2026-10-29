using System.Net;
using System.Net.Http.Json;
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
/// Shared in-memory host for integration tests: temp SQLite DB, pepper, storage dir, capturing logger.
/// Dev mail (LogEmailSender) prints OTP codes to the log, where they can be captured.
/// </summary>
internal sealed partial class TestApp : IDisposable
{
    // Matches both the English and the localised (ru) OTP mail body.
    [GeneratedRegex(@"to (?<to>[^:]+):.*?(?:Your code|Ваш код): (?<code>\d{6})", RegexOptions.Singleline, 250)]
    private static partial Regex CodeLine();

    public TestAppFactory Factory { get; }

    public TestApp(Action<IServiceCollection>? configureServices = null,
        Action<IWebHostBuilder>? configureSettings = null)
    {
        Factory = new TestAppFactory(configureServices, configureSettings);
    }

    public HttpClient CreateClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "eventpix");
        return client;
    }

    /// <summary>Same write path as the create-event CLI command. Returns the secret token and the event id.</summary>
    public async Task<(string Token, Guid EventId)> CreateEventAsync(string ownerEmail, bool inviteOnly = false)
    {
        using var scope = Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var tokens = sp.GetRequiredService<EventTokenService>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        var (token, hash, prot) = tokens.Create();
        var ev = new Event
        {
            Name = "Test event",
            OwnerEmail = ownerEmail,
            TokenHash = hash,
            TokenProtected = prot,
            InviteOnly = inviteOnly,
            CreatedUtc = now,
        };
        db.Events.Add(ev);
        db.Members.Add(new Member { EventId = ev.Id, Email = ownerEmail, DisplayName = "owner", Role = MemberRole.Owner, CreatedUtc = now });
        await db.SaveChangesAsync();
        return (token, ev.Id);
    }

    /// <summary>Full OTP sign-in for an e-mail that is already allowed (existing member or invite).</summary>
    public async Task SignInAsync(HttpClient client, string token, string email)
    {
        var requested = await client.PostAsJsonAsync($"/api/e/{token}/auth/request", new { Email = email });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var code = await WaitForCodeAsync(email);
        var verified = await client.PostAsJsonAsync($"/api/e/{token}/auth/verify", new { Email = email, Code = code });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
    }

    private async Task<string> WaitForCodeAsync(string email)
    {
        for (var i = 0; i < 50; i++)
        {
            foreach (var line in Factory.Logs.Lines)
            {
                var match = CodeLine().Match(line);
                if (match.Success && match.Groups["to"].Value == email)
                {
                    return match.Groups["code"].Value;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"No OTP logged for {email}.");
    }

    public void Dispose()
    {
        Factory.Dispose();
    }
}

internal sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<IWebHostBuilder>? _configureSettings;

    public TestAppFactory(Action<IServiceCollection>? configureServices = null,
        Action<IWebHostBuilder>? configureSettings = null)
    {
        _configureServices = configureServices;
        _configureSettings = configureSettings;
    }

    public CapturingLoggerProvider Logs { get; } = new();

    private string Root { get; } = Path.Combine(Path.GetTempPath(), "eventpix-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _configureSettings?.Invoke(builder);
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
        builder.UseSetting($"{TelegramOptions.Section}:BotToken", "");   // a local appsettings.Development.json
        builder.UseSetting($"{MaxOptions.Section}:BotToken", "");        // must not leak bot tokens into tests
        builder.ConfigureServices(services =>
        {
            _configureServices?.Invoke(services);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName)
    {
        return new Capturing(this);
    }

    public void Dispose() { }

    private sealed class Capturing(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (exception is not null)
            {
                line += "\n" + exception;
            }

            lock (owner.Lines)
            {
                owner.Lines.Add(line);
            }
        }
    }
}
