using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Theming-contract guard: every theme in the repo must ship the shell, every template and every
/// data-ref the core JS reads or wires - without any coupling from core JS to theme class names.
/// </summary>
public sealed class ThemingTests
{
    public static TheoryData<string> Themes { get; } = ["default", "mono"];

    [Theory]
    [MemberData(nameof(Themes))]
    public async Task Theme_Serves_Shell_And_Every_Template_And_Ref_The_Core_Uses(string theme)
    {
        using var baseFactory = new TestAppFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.UseSetting("EventPix:ActiveTheme", theme));
        var client = factory.CreateClient();

        var shell = await client.GetAsync(new Uri("/e/anything", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var html = await shell.Content.ReadAsStringAsync();
        Assert.Contains("/theme/theme.css", html, StringComparison.Ordinal);
        Assert.Contains("/app/app.js", html, StringComparison.Ordinal);

        var templates = await (await client.GetAsync(new Uri("/theme/templates.html", UriKind.Relative))).Content.ReadAsStringAsync();
        var appJs = await File.ReadAllTextAsync(RepoFile("src", "EventPix.Web", "wwwroot", "app", "app.js"));

        foreach (var name in Names(appJs, "tpl\\('([^']+)'"))
        {
            Assert.Contains("data-tpl=\"" + name + "\"", templates, StringComparison.Ordinal);
        }

        foreach (var name in Names(appJs, "(?:ref|text)\\([^(),\\r\\n]+,\\s*'([^']+)'\\)"))
        {
            Assert.Contains("data-ref=\"" + name + "\"", templates, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/theme/theme.css", UriKind.Relative))).StatusCode);
    }

    private static IEnumerable<string> Names(string appJs, string pattern)
    {
        return Regex.Matches(appJs, pattern).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EventPix.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine([dir, .. parts]);
    }
}
