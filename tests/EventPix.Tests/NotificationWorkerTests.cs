using System.Net;
using EventPix.Web.Data;
using EventPix.Web.Domain;
using EventPix.Web.Notifications;
using EventPix.Web.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventPix.Tests;

public sealed class NotificationWorkerTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Ready_Media_Reaches_Confirmed_Subscribers_Through_The_Outbox()
    {
        var owner = OwnerEmail();
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var seed = await SeedAsync(eventId, uploaderIsOwner: false, digest: false);

        await Tick();

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            Assert.NotNull((await db.MediaItems.FirstAsync(m => m.Id == seed.MediaId)).NotifiedUtc);

            var row = Assert.Single(await db.Outbox.ToListAsync());
            Assert.NotNull(row.SentUtc);
            Assert.Equal(seed.SubscriptionId, row.SubscriptionId);
        }

        var mail = SingleDevMail();
        Assert.Contains("New in Test event", mail, StringComparison.Ordinal);
        Assert.Contains("Greta added 1 photo(s) and 0 video(s)", mail, StringComparison.Ordinal);
        Assert.Contains("/e/" + token, mail, StringComparison.Ordinal);
        Assert.Contains("/u/" + seed.SubscriptionId.ToString("N") + ".", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploader_Never_Gets_Notified_About_Own_Uploads()
    {
        var (_, eventId) = await _app.CreateEventAsync(OwnerEmail());
        await SeedAsync(eventId, uploaderIsOwner: true, digest: false);

        await Tick();

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.Outbox.ToListAsync());
        Assert.All(await db.MediaItems.ToListAsync(), m => Assert.NotNull(m.NotifiedUtc));
    }

    [Fact]
    public async Task Digest_Subscription_Rolls_Up_Batches_Into_One_Message_Per_Window()
    {
        var (_, eventId) = await _app.CreateEventAsync(OwnerEmail());
        var seed = await SeedAsync(eventId, uploaderIsOwner: false, digest: true);

        await Tick();   // first batch opens the rolling window
        await AddMediaAsync(eventId, seed.MediaId);
        await Tick();   // second batch merges into the still-open row

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var row = Assert.Single(await db.Outbox.ToListAsync());
            Assert.Null(row.SentUtc);
            Assert.Equal(0, row.Attempts);
            Assert.Contains("\"uploader\":\"Greta\"", row.GroupsJson, StringComparison.Ordinal);
            Assert.Contains("\"photos\":2", row.GroupsJson, StringComparison.Ordinal);
            Assert.Contains("\"photos\":2", row.GroupsJson, StringComparison.Ordinal);
        }

        Assert.Empty(DevMails());
        await BumpDigestDue();
        await Tick();

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            Assert.NotNull((Assert.Single(await db.Outbox.ToListAsync())).SentUtc);
        }

        var mail = SingleDevMail();
        Assert.Contains("New in Test event", mail, StringComparison.Ordinal);
        Assert.Contains("Greta added 2 photo(s)", mail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsubscribe_Drops_Pending_Outbox_Rows()
    {
        var owner = OwnerEmail();
        var (token, eventId) = await _app.CreateEventAsync(owner);
        var seed = await SeedAsync(eventId, uploaderIsOwner: false, digest: true);

        await Tick();

        var client = _app.CreateClient();
        await _app.SignInAsync(client, token, owner);
        var path = "/api/e/" + token + "/subscriptions/" + seed.SubscriptionId;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(U(path))).StatusCode);

        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        Assert.Empty(await db.Outbox.ToListAsync());
        Assert.Empty(await db.Subscriptions.ToListAsync());
    }

    [Fact]
    public async Task Failed_Sends_Retry_With_Backoff_And_Deliver_Later()
    {
        var flaky = new FlakySender();
        using var baseFactory = new TestAppFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IEmailSender>(flaky)));
        await SeedOnFactoryAsync(factory);

        var worker = factory.Services.GetServices<IHostedService>().OfType<NotificationWorker>().Single();
        await worker.TickAsync(CancellationToken.None);

        long rowId;
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var row = await db.Outbox.SingleAsync();
            Assert.Null(row.SentUtc);
            Assert.Equal(1, row.Attempts);
            Assert.True(row.NotBeforeUtc > DateTime.UtcNow.AddMinutes(1));   // about 2 minutes of backoff
            rowId = row.Id;
        }

        await worker.TickAsync(CancellationToken.None);   // not due yet: attempt count unchanged
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            Assert.Equal(1, (await db.Outbox.SingleAsync(r => r.Id == rowId)).Attempts);
        }

        flaky.Failures = 0;
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var row = await db.Outbox.SingleAsync(r => r.Id == rowId);
            row.NotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var delivered = await db.Outbox.SingleAsync(r => r.Id == rowId);
            Assert.NotNull(delivered.SentUtc);
            Assert.Equal(2, delivered.Attempts);
        }
    }
    private static Uri U(string path)
    {
        return new(path, UriKind.Relative);
    }


    [Fact]
    public async Task Missing_Channel_Keeps_The_Row_Pending()
    {
        var owner = OwnerEmail();
        var (_, eventId) = await _app.CreateEventAsync(owner);
        var seed = await SeedAsync(eventId, uploaderIsOwner: false, digest: false);

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var sub = await db.Subscriptions.SingleAsync(s => s.Id == seed.SubscriptionId);
            sub.Channel = (NotifyChannel)99;   // no channel is registered for this value
            await db.SaveChangesAsync();
        }

        await Tick();

        using (var scope = _app.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            var row = await db.Outbox.SingleAsync();
            Assert.Null(row.SentUtc);
            Assert.Equal(0, row.Attempts);
        }
    }

    [Fact]
    public async Task Exhausted_Retries_Drop_The_Row()
    {
        var flaky = new FlakySender { Failures = NotificationWorker.MaxAttempts };
        using var baseFactory = new TestAppFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(
            s => s.AddSingleton<IEmailSender>(flaky)));
        await SeedOnFactoryAsync(factory);

        var worker = factory.Services.GetServices<IHostedService>().OfType<NotificationWorker>().Single();
        await worker.TickAsync(CancellationToken.None);   // dispatch + first failure
        for (var attempt = 1; attempt < NotificationWorker.MaxAttempts; attempt++)
        {
            using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
                var row = await db.Outbox.SingleAsync();
                row.NotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }

            await worker.TickAsync(CancellationToken.None);
        }

        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
            Assert.Empty(await db.Outbox.ToListAsync());
        }

        Assert.Contains(baseFactory.Logs.Lines, l => l.Contains("Notification dropped after", StringComparison.Ordinal));
    }
    private Task Tick()
    {
        var worker = _app.Factory.Services.GetServices<IHostedService>().OfType<NotificationWorker>().Single();
        return worker.TickAsync(CancellationToken.None);
    }

    private List<string> DevMails()
    {
        return _app.Factory.Logs.Lines
            .Where(l => l.StartsWith("DEV MAIL to", StringComparison.Ordinal)).ToList();
    }

    private string SingleDevMail()
    {
        return Assert.Single(DevMails());
    }

    private async Task BumpDigestDue()
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var row = await db.Outbox.SingleAsync();
        row.NotBeforeUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private static string OwnerEmail()
    {
        return "owner-" + Guid.NewGuid().ToString("N") + "@example.com";
    }

    private async Task<(Guid SubscriptionId, Guid MediaId)> SeedAsync(
        Guid eventId, bool uploaderIsOwner, bool digest)
    {
        var now = DateTime.UtcNow.AddMinutes(-1);
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var owner = await db.Members.FirstAsync(m => m.EventId == eventId);

        var uploader = owner;
        if (!uploaderIsOwner)
        {
            uploader = new Member
            {
                EventId = eventId,
                Email = "guest-" + Guid.NewGuid().ToString("N") + "@example.com",
                DisplayName = "Greta",
                Role = MemberRole.Guest,
                CreatedUtc = now,
            };
            db.Members.Add(uploader);
        }

        var tab = new Tab
        {
            EventId = eventId,
            Name = "Author",
            Kind = TabKind.Author,
            SortOrder = 99,
            OwnerMemberId = uploaderIsOwner ? null : uploader.Id,
        };
        var media = NewMedia(eventId, tab.Id, uploader.Id, now);
        var sub = new Subscription
        {
            EventId = eventId,
            MemberId = owner.Id,
            Channel = NotifyChannel.Email,
            Address = owner.Email,
            TabId = null,
            Mode = digest ? NotifyMode.Digest : NotifyMode.Instant,
            Confirmed = true,
            CreatedUtc = now,
        };
        db.Tabs.Add(tab);
        db.MediaItems.Add(media);
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (sub.Id, media.Id);
    }

    private async Task AddMediaAsync(Guid eventId, Guid afterMediaId)
    {
        using var scope = _app.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
        var first = await db.MediaItems.FirstAsync(m => m.Id == afterMediaId);
        db.MediaItems.Add(NewMedia(eventId, first.TabId, first.UploaderId, DateTime.UtcNow.AddMinutes(-1)));
        await db.SaveChangesAsync();
    }

    private static MediaItem NewMedia(Guid eventId, Guid tabId, Guid uploaderId, DateTime now)
    {
        return new MediaItem
        {
            EventId = eventId,
            TabId = tabId,
            UploaderId = uploaderId,
            Kind = MediaKind.Photo,
            Status = MediaStatus.Ready,
            OriginalFileName = "a.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 3,
            Sha256 = "hash",
            StorageKey = eventId.ToString("N") + "/" + Guid.NewGuid().ToString("N"),
            UploadedUtc = now.AddMinutes(-30),   // older than the 5-minute debounce
        };
    }

    private static async Task SeedOnFactoryAsync(WebApplicationFactory<Program> factory)
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<EventPixDb>();
        var tokenService = sp.GetRequiredService<EventTokenService>();

        var created = tokenService.Create();
        var ev = new Event
        {
            Name = "Test event",
            OwnerEmail = "owner@example.com",
            TokenHash = created.Hash,
            TokenProtected = created.Protected,
            CreatedUtc = now,
        };
        var owner = new Member
        {
            EventId = ev.Id,
            Email = "owner@example.com",
            DisplayName = "owner",
            Role = MemberRole.Owner,
            CreatedUtc = now,
        };
        var guest = new Member
        {
            EventId = ev.Id,
            Email = "guest@example.com",
            DisplayName = "Greta",
            Role = MemberRole.Guest,
            CreatedUtc = now,
        };
        var tab = new Tab
        {
            EventId = ev.Id,
            Name = "Author",
            Kind = TabKind.Author,
            SortOrder = 99,
            OwnerMemberId = guest.Id,
        };
        var media = NewMedia(ev.Id, tab.Id, guest.Id, now);
        var sub = new Subscription
        {
            EventId = ev.Id,
            MemberId = owner.Id,
            Channel = NotifyChannel.Email,
            Address = owner.Email,
            TabId = null,
            Mode = NotifyMode.Instant,
            Confirmed = true,
            CreatedUtc = now,
        };
        db.AddRange(ev, owner, guest, tab, media, sub);
        await db.SaveChangesAsync();
    }

    internal sealed class FlakySender : IEmailSender
    {
        public int Failures = 1;

        public Task SendAsync(string to, string subject, string textBody, CancellationToken ct)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new InvalidOperationException("smtp down");
            }

            return Task.CompletedTask;
        }
    }
}
