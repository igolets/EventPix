using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// One Chromium instance shared by all E2E tests. Downloads the browser on first use
/// (cached per user profile), so `dotnet test` works on dev machines and CI alike.
/// </summary>
[SuppressMessage("Design", "CA1515", Justification = "xUnit fixture wiring requires public accessibility.")]
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IBrowser Browser { get; private set; } = null!;

    private IPlaywright Playwright { get; set; } = null!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        try
        {
            Browser = await Playwright.Chromium.LaunchAsync();
        }
        catch (PlaywrightException)
        {
            // First run on this machine/runner: fetch Chromium, then launch again.
            Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
            Browser = await Playwright.Chromium.LaunchAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        Playwright?.Dispose();
    }
}

[CollectionDefinition("playwright")]
[SuppressMessage("Design", "CA1515", Justification = "xUnit fixture wiring requires public accessibility.")]
[SuppressMessage("Naming", "CA1711", Justification = "xUnit convention: this marker IS the collection definition.")]
public sealed class PlaywrightCollectionDefinition : ICollectionFixture<PlaywrightFixture>;
