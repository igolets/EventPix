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
public sealed partial class E2ETests(PlaywrightFixture fixture)
{
    [GeneratedRegex(@"/e/([A-Za-z0-9_-]+)")]
    private static partial Regex EventTokenRegex();

    [GeneratedRegex("/thumb$")]
    private static partial Regex ThumbSrcRegex();

    [GeneratedRegex(@"photo-[0-9a-f]{32}\.jpg")]
    private static partial Regex PhotoCaptionRegex();

    [Fact]
    public async Task Login_Upload_Star_Download_Full_Flow()
    {
        await using var app = new EventPixAppHost();
        await app.StartAsync();
        var ownerEmail = $"e2e-{Guid.NewGuid():N}@example.com";

        var (exit, stdout, _) = await app.RunCommandAsync("create-event", "E2E Event", ownerEmail, "--invite-only");   // login wall: guest browsing is covered by GuestViewingTests
        Assert.Equal(0, exit);
        var token = EventTokenRegex().Match(stdout).Groups[1].Value;
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
            await Assertions.Expect(page.Locator("[data-i18n='upload']")).ToHaveTextAsync("Add photos & videos");

            // Upload a generated JPEG (no committed media): EXIF orientation 6 -> stored upright.
            var jpegPath = Path.Combine(Path.GetTempPath(), $"photo-{Guid.NewGuid():N}.jpg");
            await File.WriteAllBytesAsync(jpegPath, TestJpeg.Asymmetric(6));
            try
            {
                await page.Locator("[data-ref='upload-input']").SetInputFilesAsync(jpegPath);
                await Assertions.Expect(page.Locator("[data-ref='status']")).ToContainTextAsync("Uploaded");

                // The status note clears again once the worker has produced every thumbnail.
                await Assertions.Expect(page.Locator("[data-ref='status']")).ToHaveTextAsync("", new() { Timeout = 20_000 });

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

                await Assertions.Expect(image).ToHaveAttributeAsync("src", ThumbSrcRegex());

                // Lightbox shows the upright preview and names the file.
                await page.Locator("[data-ref='grid'] [data-ref='open']").First.ClickAsync();
                await Assertions.Expect(page.Locator("[data-ref='stage'] img")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator("[data-ref='caption']"))
                    .ToContainTextAsync(PhotoCaptionRegex());
                await page.Locator("[data-ref='close']").ClickAsync();

                // Star the tile; the state attribute is the CSS/theming hook.
                await page.Locator("[data-ref='grid'] [data-ref='star']").First.ClickAsync();
                await Assertions.Expect(page.Locator("[data-ref='grid'] [data-starred='true']").First).ToBeVisibleAsync();

                // Lightbox zoom: wheel over the stage flips the [data-zoom] state hook.
                await page.Locator("[data-ref='grid'] [data-ref='open']").First.ClickAsync();
                var stage = page.Locator("[data-tpl='lightbox'] [data-ref='stage']");
                await stage.HoverAsync();
                await page.Mouse.WheelAsync(0, -400);
                await Assertions.Expect(page.Locator("[data-tpl='lightbox']")).ToHaveAttributeAsync("data-zoom", "true");
                await page.Locator("[data-ref='close']").ClickAsync();

                // Download this tab as a ZIP through the browser.
                var download = await page.RunAndWaitForDownloadAsync(async () =>
                    await page.Locator("[data-ref='download-tab']").ClickAsync());
                Assert.True(new FileInfo(await download.PathAsync()).Length > 0);

                // Multi-select: one selected tile -> POST /download through the browser.
                await page.Locator("[data-ref='select']").ClickAsync();
                await Assertions.Expect(page.Locator("[data-tpl='gallery']")).ToHaveAttributeAsync("data-selecting", "true");
                await page.Locator("[data-ref='grid'] [data-ref='open']").First.ClickAsync();   // toggles selection
                var dlSelected = page.Locator("[data-ref='download-selected']");
                await Assertions.Expect(dlSelected).ToBeVisibleAsync();
                var selection = await page.RunAndWaitForDownloadAsync(async () => await dlSelected.ClickAsync());
                Assert.True(new FileInfo(await selection.PathAsync()).Length > 0);

                await page.Locator("[data-ref='select']").ClickAsync();
                await Assertions.Expect(page.Locator("[data-tpl='gallery']")).ToHaveAttributeAsync("data-selecting", "false");
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
        var token = EventTokenRegex().Match(stdout).Groups[1].Value;

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

    [Fact]
    public async Task Strings_Localise_Via_Lang_Param()
    {
        await using var app = new EventPixAppHost();
        await app.StartAsync();
        var ownerEmail = $"e2e-{Guid.NewGuid():N}@example.com";
        var (_, stdout, _) = await app.RunCommandAsync("create-event", "E2E Event", ownerEmail, "--invite-only");   // keep the login page up: guest viewing is covered by the API tests
        var token = EventTokenRegex().Match(stdout).Groups[1].Value;

        var context = await fixture.Browser.NewContextAsync();
        await using (context)
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{app.BaseAddress}e/{token}?lang=ru");

            // Static theme labels are localised through data-i18n, not just core-written strings.
            await Assertions.Expect(page.Locator("[data-i18n='eyebrow']")).ToHaveTextAsync("Общий альбом");
            await Assertions.Expect(page.Locator("[data-i18n='sendCode']")).ToHaveTextAsync("Отправить код");

            // Well-formed for the browser, too long for the server (max 254): triggers the error path.
            await page.Locator("[data-ref='email']").FillAsync("a@" + string.Join(".", Enumerable.Repeat("ab", 100)) + ".com");
            await page.Locator("[data-ref='email-form'] button[type='submit']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-ref='message']")).ToContainTextAsync("Проверьте адрес");
        }
    }

    [Fact]
    public async Task Drag_And_Drop_Uploads_For_Members_And_Sends_Guests_To_Login()
    {
        await using var app = new EventPixAppHost();
        await app.StartAsync();
        var ownerEmail = $"e2e-{Guid.NewGuid():N}@example.com";

        // Open event: guests may browse, but a dropped file must never upload without a session.
        var (_, open, _) = await app.RunCommandAsync("create-event", "E2E Open Album", ownerEmail);
        var openToken = EventTokenRegex().Match(open).Groups[1].Value;

        // Invite-only event: the login wall shows naturally so the member can sign in through the UI.
        var (_, wall, _) = await app.RunCommandAsync("create-event", "E2E Walled Album", ownerEmail, "--invite-only");
        var wallToken = EventTokenRegex().Match(wall).Groups[1].Value;

        // Synthetic drag events carrying a real DataTransfer; CDP evaluation is not subject to the CSP.
        var jpeg = Convert.ToBase64String(TestJpeg.Asymmetric());
        const string dragEnter = """
            (b64) => {
              const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
              const dt = new DataTransfer();
              dt.items.add(new File([bytes], 'dropped.jpg', { type: 'image/jpeg' }));
              window.__drop = dt;
              document.querySelector("[data-tpl='gallery']")
                .dispatchEvent(new DragEvent('dragenter', { bubbles: true, cancelable: true, dataTransfer: dt }));
            }
            """;
        const string dragDrop = """
            () => {
              const view = document.querySelector("[data-tpl='gallery']");
              const init = { bubbles: true, cancelable: true, dataTransfer: window.__drop };
              view.dispatchEvent(new DragEvent('dragover', init));
              view.dispatchEvent(new DragEvent('drop', init));
            }
            """;

        var context = await fixture.Browser.NewContextAsync();
        await using (context)
        {
            // Guest: the drop is refused and the OTP wall comes up instead.
            var guest = await context.NewPageAsync();
            await guest.GotoAsync($"{app.BaseAddress}e/{openToken}");
            await Assertions.Expect(guest.Locator("[data-tpl='gallery']")).ToBeVisibleAsync();
            await guest.EvaluateAsync(dragEnter, jpeg);
            await Assertions.Expect(guest.Locator("[data-tpl='gallery']")).ToHaveAttributeAsync("data-dragover", "true");
            await Assertions.Expect(guest.Locator("[data-tpl='gallery']")).ToHaveAttributeAsync("data-dropallowed", "false");
            await Assertions.Expect(guest.Locator("[data-ref='dropzone-text']")).ToHaveTextAsync("Sign in to add photos");
            await guest.EvaluateAsync(dragDrop);
            await Assertions.Expect(guest.Locator("[data-ref='email']")).ToBeVisibleAsync();

            // Member: the drop uploads into the current tab, exactly like the file picker.
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{app.BaseAddress}e/{wallToken}");
            await page.Locator("[data-ref='email']").FillAsync(ownerEmail);
            await page.Locator("[data-ref='email-form'] button[type='submit']").ClickAsync();
            await page.Locator("[data-ref='code']").FillAsync(await app.WaitForCodeAsync(ownerEmail));
            await page.Locator("[data-ref='code-form'] button[type='submit']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-tpl='gallery']")).ToBeVisibleAsync();
            // Fresh event: no tabs yet, so the drop must behave like the picker and hit the own Author tab.
            await page.EvaluateAsync(dragEnter, jpeg);
            await Assertions.Expect(page.Locator("[data-tpl='gallery']")).ToHaveAttributeAsync("data-dropallowed", "true");
            await page.EvaluateAsync(dragDrop);
            await Assertions.Expect(page.Locator("[data-ref='status']")).ToContainTextAsync("Uploaded");
        }
    }

    /// <summary>Mirrors the notification link format: HMAC(pepper, "unsub|{id:N}") as hex.</summary>
    private static string UnsubscribeSignature(string dataPath, Guid id)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Convert.FromBase64String(File.ReadAllText(Path.Combine(dataPath, "pepper.key")).Trim()));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("unsub|" + id.ToString("N"))));
    }
}
