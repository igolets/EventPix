using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace EventPix.Tests;

/// <summary>
/// Browser E2E: the real app (Kestrel process) driven through the theme's data-ref hooks —
/// the same contract a custom theme must keep. Selectors never rely on styling classes.
/// </summary>
[Collection("playwright")]
public sealed class E2ETests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task Login_Upload_Star_Download_Full_Flow()
    {
        await using var app = new EventPixAppHost();
        await app.StartAsync();
        var ownerEmail = $"e2e-{Guid.NewGuid():N}@example.com";

        var (exit, stdout, _) = await app.RunCommandAsync("create-event", "E2E Event", ownerEmail);
        Assert.Equal(0, exit);
        var token = Regex.Match(stdout, @"/e/([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEmpty(token);

        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });
        await using (context)
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{app.BaseAddress}e/{token}");

            // Login: the theme shell shows the event name, then the OTP exchange.
            await Assertions.Expect(page.Locator("[data-ref='event-name']")).ToHaveTextAsync("E2E Event");
            await page.Locator("[data-ref='email']").FillAsync(ownerEmail);
            await page.Locator("[data-ref='email-form'] button[type='submit']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-ref='code']")).ToBeVisibleAsync();

            await page.Locator("[data-ref='code']").FillAsync(await app.WaitForCodeAsync(ownerEmail));
            await page.Locator("[data-ref='code-form'] button[type='submit']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-ref='upload-input']")).ToBeAttachedAsync();

            // Upload a generated JPEG (no committed media): EXIF orientation 6 -> stored upright.
            var jpegPath = Path.Combine(Path.GetTempPath(), $"photo-{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(jpegPath, TestJpeg.Asymmetric(6));
            try
            {
                await page.Locator("[data-ref='upload-input']").SetInputFilesAsync(jpegPath);
                await Assertions.Expect(page.Locator("[data-ref='status']")).ToContainTextAsync("Uploaded");

                // The worker builds thumbnails in the background; the grid re-renders on reload.
                var image = page.Locator("[data-ref='grid'] [data-ref='image']").First;
                for (var i = 0; i < 40; i++)
                {
                    await page.ReloadAsync();
                    if (await image.CountAsync() > 0 && !string.IsNullOrEmpty(await image.GetAttributeAsync("src")))
                    {
                        break;
                    }

                    await Task.Delay(500);
                }

                await Assertions.Expect(image).ToHaveAttributeAsync("src", new Regex("/thumb$"));

                // Lightbox shows the upright preview and names the file.
                await page.Locator("[data-ref='grid'] [data-ref='open']").First.ClickAsync();
                await Assertions.Expect(page.Locator("[data-ref='stage'] img")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-ref='caption']"))
                    .ToContainTextAsync(new Regex(@"photo-[0-9a-f]{32}\.jpg"));
                await page.Locator("[data-ref='close']").ClickAsync();

                // Star the tile; the state attribute is the CSS/theming hook.
                await page.Locator("[data-ref='grid'] [data-ref='star']").First.ClickAsync();
                await Assertions.Expect(page.Locator("[data-ref='grid'] [data-starred='true']").First).ToBeVisibleAsync();

                // Download this tab as a ZIP through the browser.
                var download = await page.RunAndWaitForDownloadAsync(async () =>
                    await page.Locator("[data-ref='download-tab']").ClickAsync());
                Assert.True(new FileInfo(await download.PathAsync()).Length > 0);
            }
            finally
            {
                File.Delete(jpegPath);
            }
        }
    }

    [Fact]
    public async Task Unsubscribe_Page_Confirms_And_Rejects_Bad_Signature()
    {
        await using var app = new EventPixAppHost();
        await app.StartAsync();
        var ownerEmail = $"e2e-{Guid.NewGuid():N}@example.com";
        var (_, stdout, _) = await app.RunCommandAsync("create-event", "E2E Event", ownerEmail);
        var token = Regex.Match(stdout, @"/e/([A-Za-z0-9_-]+)").Groups[1].Value;

        using var http = app.NewHttpClient();
        await app.SignInAsync(http, token, ownerEmail);
        var put = await http.PutAsJsonAsync($"api/e/{token}/subscriptions",
            new { Channel = "Email", TabId = (Guid?)null, Mode = "Instant" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var subId = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var context = await fixture.Browser.NewContextAsync();
        await using (context)
        {
            var page = await context.NewPageAsync();

            await page.GotoAsync($"{app.BaseAddress}u/{subId:N}.{UnsubscribeSignature(app.DataPath, subId)}");
            await Assertions.Expect(page.Locator("[data-ref='message']")).ToContainTextAsync("unsubscribed");

            // A forged signature never unsubscribes anything.
            await page.GotoAsync($"{app.BaseAddress}u/{subId:N}.{new string('0', 64)}");
            await Assertions.Expect(page.Locator("[data-ref='message']")).ToContainTextAsync("not valid");
        }

        var subs = await http.GetFromJsonAsync<JsonElement>($"api/e/{token}/subscriptions");
        Assert.Empty(subs.EnumerateArray());
    }

    /// <summary>Mirrors the notification link format: HMAC(pepper, "unsub|{id:N}") as hex.</summary>
    private static string UnsubscribeSignature(string dataPath, Guid id)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Convert.FromBase64String(File.ReadAllText(Path.Combine(dataPath, "pepper.key")).Trim()));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("unsub|" + id.ToString("N"))));
    }
}
