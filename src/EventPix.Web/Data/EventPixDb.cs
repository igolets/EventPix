using EventPix.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPix.Web.Data;

internal sealed class EventPixDb(DbContextOptions<EventPixDb> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Tab> Tabs => Set<Tab>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<Star> Stars => Set<Star>();
    public DbSet<LoginCode> LoginCodes => Set<LoginCode>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<TelegramLinkCode> TelegramLinkCodes => Set<TelegramLinkCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Event>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<Member>(m =>
        {
            m.Property(x => x.Email).HasMaxLength(254);
            m.Property(x => x.DisplayName).HasMaxLength(100);
            m.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            m.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
        });

        b.Entity<Invite>(i =>
        {
            i.Property(x => x.Email).HasMaxLength(254);
            i.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            i.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
        });

        b.Entity<Tab>(t =>
        {
            t.Property(x => x.Name).HasMaxLength(100);
            t.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            t.HasIndex(x => new { x.EventId, x.SortOrder });
            t.HasIndex(x => new { x.EventId, x.OwnerMemberId, x.Name }).IsUnique()
                .HasFilter($"[Kind] = {(int)TabKind.Author}");   // a member's author tabs have unique names
        });

        b.Entity<MediaItem>(m =>
        {
            m.Property(x => x.OriginalFileName).HasMaxLength(255);
            m.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            m.HasOne<Tab>().WithMany().HasForeignKey(x => x.TabId).OnDelete(DeleteBehavior.Cascade);
            m.HasOne<Member>().WithMany().HasForeignKey(x => x.UploaderId).OnDelete(DeleteBehavior.Restrict);
            m.HasIndex(x => x.EventId);
            m.HasIndex(x => x.NotifiedUtc);
            m.HasIndex(x => x.UploaderId);
            m.HasIndex(x => new { x.TabId, x.Sha256 });
            m.HasIndex(x => new { x.TabId, x.UploadedUtc });
        });

        b.Entity<Star>(s =>
        {
            s.HasKey(x => new { x.MemberId, x.MediaId });
            s.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
            s.HasOne<MediaItem>().WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
            s.HasIndex(x => x.MediaId);
        });

        b.Entity<LoginCode>(l =>
        {
            l.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            l.HasIndex(x => new { x.EventId, x.Email, x.CreatedUtc });
        });

        b.Entity<Subscription>(s =>
        {
            s.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            s.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
            s.HasIndex(x => x.MemberId);
            s.HasIndex(x => new { x.EventId, x.MemberId, x.Channel, x.TabId }).IsUnique();
        });

        b.Entity<OutboxMessage>(o =>
        {
            o.HasOne<Subscription>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            o.Property(x => x.EventName).HasMaxLength(200);
            o.HasIndex(x => new { x.SentUtc, x.NotBeforeUtc });
        });

        b.Entity<TelegramLinkCode>(c =>
        {
            c.HasIndex(x => x.CodeHash).IsUnique();
            c.HasOne<Event>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            c.HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
            c.HasIndex(x => x.ExpiresUtc);
        });
    }
}
