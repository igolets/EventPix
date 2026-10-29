namespace EventPix.Web.Domain;

internal enum MemberRole { Guest = 0, Owner = 1 }
internal enum TabKind { Section = 0, Author = 1 }
internal enum MediaKind { Photo = 0, Video = 1 }
internal enum MediaStatus { Processing = 0, Ready = 1, Failed = 2 }
internal enum NotifyChannel { Email = 0, Telegram = 1, Max = 2 }
internal enum NotifyMode { Instant = 0, Digest = 1 }

/// <summary>One shared album (a wedding, a graduation...). Reached via a secret URL token.</summary>
internal sealed class Event
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>SHA-256 of the URL token. Used for lookup; the raw token is never stored in clear.</summary>
    public required string TokenHash { get; set; }
    /// <summary>URL token encrypted with Data Protection, so notifications can build links.</summary>
    public required string TokenProtected { get; set; }
    public required string OwnerEmail { get; set; }
    /// <summary>true: only pre-registered emails may sign in. false: anyone with the link + a valid email.</summary>
    public bool InviteOnly { get; set; }
    public bool UploadsOpen { get; set; } = true;
    public bool DownloadsOpen { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One participant identity. Either an e-mail member (Email set) or a messenger member signed in
/// via a bot (MessengerChannel + MessengerId set). The same person may exist as several members;
/// uploads and stars always attribute to the identity that was actually signed in.
/// </summary>
internal sealed class Member
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public string? Email { get; set; }                  // normalised, lower-case; null for messenger members
    public NotifyChannel? MessengerChannel { get; set; }
    /// <summary>The messenger platform's stable user id (as string), proven by the bot.</summary>
    public string? MessengerId { get; set; }
    /// <summary>@handle at sign-in time (informational; identity is MessengerId — handles can change).</summary>
    public string? MessengerHandle { get; set; }
    public required string DisplayName { get; set; }
    public MemberRole Role { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

/// <summary>
/// A pre-registered identity for InviteOnly events; becomes a Member on first sign-in. Either an
/// e-mail (Email set) or a messenger handle (MessengerChannel + MessengerHandle set), matched by the
/// bot on the member's first Start.
/// </summary>
internal sealed class Invite
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public string? Email { get; set; }                  // normalised, lower-case; null for messenger invites
    public NotifyChannel? MessengerChannel { get; set; }
    /// <summary>Normalised handle (lower-case, no @); the member must have the same handle at sign-in.</summary>
    public string? MessengerHandle { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A named tab: either an event part ("Ceremony") or a person's own uploads.</summary>
internal sealed class Tab
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public required string Name { get; set; }
    public TabKind Kind { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Set for Author tabs.</summary>
    public Guid? OwnerMemberId { get; set; }
    /// <summary>Section tabs only: may guests upload here?</summary>
    public bool AllowGuestUploads { get; set; }
}

internal sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid TabId { get; set; }
    public Guid UploaderId { get; set; }
    public MediaKind Kind { get; set; }
    public MediaStatus Status { get; set; }
    /// <summary>Metadata only. NEVER used to build a storage path.</summary>
    public required string OriginalFileName { get; set; }
    public required string ContentType { get; set; }      // from content sniffing, not from the client
    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public DateTime? TakenUtc { get; set; }
    public required string Sha256 { get; set; }
    /// <summary>Relative key prefix, e.g. "{eventId:N}/{id:N}". Variants: /original, /preview.jpg, /thumb.jpg.</summary>
    public required string StorageKey { get; set; }
    public DateTime UploadedUtc { get; set; }
    public DateTime? NotifiedUtc { get; set; }
}

internal sealed class Star
{
    public Guid MemberId { get; set; }
    public Guid MediaId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

internal sealed class LoginCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public required string Email { get; set; }
    public required string CodeHash { get; set; }          // HMAC(pepper, eventId|email|code)
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}

internal sealed class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }
    public Guid MemberId { get; set; }
    public NotifyChannel Channel { get; set; }
    /// <summary>Email address, or the bot chat id for Telegram/MAX.</summary>
    public required string Address { get; set; }
    /// <summary>null = everything in the event.</summary>
    public Guid? TabId { get; set; }
    public NotifyMode Mode { get; set; }
    public bool Confirmed { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One pending or delivered notification. Instant dispatch writes one row per (tab, uploader) batch;
/// a digest subscription has at most one unsent row whose groups accumulate until the window ends.
/// Cascades away with the subscription, so unsubscribing also drops pending deliveries.
/// </summary>
internal sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public NotifyChannel Channel { get; set; }
    /// <summary>Copy of the subscription address at dispatch time.</summary>
    public required string Address { get; set; }
    public required string EventName { get; set; }
    public required string Url { get; set; }
    public required string UnsubscribeUrl { get; set; }
    /// <summary>JSON list of {tab, uploader, photos, videos}; grows while a digest window is open.</summary>
    public required string GroupsJson { get; set; }
    /// <summary>Earliest delivery attempt: window end for digest, backoff pointer after a failure.</summary>
    public DateTime NotBeforeUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One pending bot sign-in (Telegram/MAX). The browser mints it, the member confirms it by pressing
/// Start in the bot with the deep-link payload, and the polling browser exchanges the confirmation
/// for a session. The raw code is shown once in the deep link; only its hash is stored.
/// </summary>
internal sealed class LoginRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Which bot confirms the sign-in.</summary>
    public NotifyChannel Channel { get; set; }
    /// <summary>SHA-256 (hex) of the deep-link payload.</summary>
    public required string CodeHash { get; set; }
    public Guid EventId { get; set; }
    /// <summary>Set when the bot proved the chat belongs to a member; the poll then consumes the request.</summary>
    public Guid? MemberId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}

/// <summary>Single-use bot linking code (Telegram/MAX). The raw code is shown once as a deep link; only its hash is stored.</summary>
internal sealed class BotLinkCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Which bot the code belongs to; consuming it confirms a subscription on the same channel.</summary>
    public NotifyChannel Channel { get; set; }
    /// <summary>SHA-256 (hex) of the deep-link payload.</summary>
    public required string CodeHash { get; set; }
    public Guid EventId { get; set; }
    public Guid MemberId { get; set; }
    /// <summary>null = whole event.</summary>
    public Guid? TabId { get; set; }
    public NotifyMode Mode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
}
