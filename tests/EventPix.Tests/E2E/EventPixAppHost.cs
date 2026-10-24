using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Runs the REAL app as a Kestrel process on a random loopback port with temp data/media dirs.
/// Playwright needs a real HTTP listener (the integration tests' TestServer opens no socket).
/// Config flows through environment variables; OTP codes are captured from the console log.
/// </summary>
internal sealed class EventPixAppHost : IAsyncDisposable
{
    private static readonly Regex _listening =
        new(@"Now listening on: (http://127\.0\.0\.1:\d+)", RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly List<string> _output = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eventpix-e2e-" + Guid.NewGuid().ToString("N"));
    private Process? _process;
    private HttpClient? _http;

    private string WebDll { get; } = FindWebDll();
    private string ThemesPath { get; } = FindThemesPath();

    public string BaseAddress { get; private set; } = "";

    public string DataPath { get; }

    public EventPixAppHost()
    {
        DataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(DataPath);
        Directory.CreateDirectory(Path.Combine(_root, "media"));
    }

    /// <summary>The dotnet driver: the test host's ProcessPath only when it really is dotnet.</summary>
    private static string DotNetExe =>
        Environment.ProcessPath is { } path &&
        Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? path
            : "dotnet";

    public async Task StartAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotNetExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(WebDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        ApplyEnvironment(psi.Environment);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("App process did not start.");
        _process.OutputDataReceived += (_, e) => Capture(e.Data);
        _process.ErrorDataReceived += (_, e) => Capture(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            string? address;
            lock (_sync)
            {
                address = _output.Select(l => _listening.Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            }

            if (address is not null)
            {
                BaseAddress = address.TrimEnd('/') + "/";
                _http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
                await WaitForReadyAsync();
                return;
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException($"App exited with {_process.ExitCode}:\n{Output()}");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"App never listened:\n{Output()}");
    }

    /// <summary>Runs a CLI command (same environment) and returns exit code with captured output.</summary>
    public async Task<(int Exit, string StdOut, string StdErr)> RunCommandAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotNetExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(WebDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        ApplyEnvironment(psi.Environment);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("CLI process did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Capture(await stdoutTask);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    public async Task<string> WaitForCodeAsync(string email)
    {
        var rx = new Regex(Regex.Escape($"to {email}:") + ".*?Your code: (\\d{6})",
            RegexOptions.Singleline | RegexOptions.Compiled);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var match = rx.Match(Output());
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No OTP logged for {email}.");
    }

    /// <summary>Signs a member in over the real HTTP API (cookie kept in the caller's HttpClient).</summary>
    public async Task SignInAsync(HttpClient client, string token, string email)
    {
        var requested = await client.PostAsJsonAsync($"api/e/{token}/auth/request", new { Email = email });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, requested.StatusCode);
        var verified = await client.PostAsJsonAsync($"api/e/{token}/auth/verify",
            new { Email = email, Code = await WaitForCodeAsync(email) });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, verified.StatusCode);
    }

    [SuppressMessage("Reliability", "CA2000",
        Justification = "The HttpClient owns the handler (disposeHandler: true).")]
    public HttpClient NewHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            CheckCertificateRevocationList = true,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(BaseAddress),
            DefaultRequestHeaders = { { "X-Requested-With", "eventpix" } },
        };
    }

    private void ApplyEnvironment(IDictionary<string, string?> env)
    {
        env["ASPNETCORE_ENVIRONMENT"] = "Development";   // LogEmailSender prints OTP codes
        env["EventPix__DataPath"] = DataPath;
        env["EventPix__StoragePath"] = Path.Combine(_root, "media");
        env["EventPix__ThemesPath"] = ThemesPath;
    }

    private void Capture(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_sync)
        {
            _output.Add(line);
        }
    }

    private string Output()
    {
        lock (_sync)
        {
            return string.Join("\n", _output);
        }
    }

    private async Task WaitForReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await _http!.GetAsync(new Uri("robots.txt", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"App never became ready:\n{Output()}");
    }

    private static string FindWebDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EventPix.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Repo root (EventPix.slnx) not found from test directory.");
        }

        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var dll = Path.Combine(dir, "src", "EventPix.Web", "bin", configuration, "net10.0", "EventPix.Web.dll");
        return File.Exists(dll)
            ? dll
            : throw new FileNotFoundException($"Build the app first (expected {dll}).");
    }

    private static string FindThemesPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "themes", "default", "index.html")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir is null
            ? throw new InvalidOperationException("Themes folder not found from test directory.")
            : Path.Combine(dir, "themes");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        _process?.Dispose();
        _http?.Dispose();

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        await Task.CompletedTask;
    }
}
