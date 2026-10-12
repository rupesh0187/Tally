using TallyNext.Contracts.Sync;

namespace TallyNext.Infrastructure.Local.Persistence;

public enum OutboxStatus : byte
{
    /// <summary>Waiting to be uploaded.</summary>
    Pending = 1,

    /// <summary>Sent; awaiting the server's answer. Reset to Pending on startup (the server deduplicates retries).</summary>
    InFlight = 2,

    /// <summary>The server accepted the change.</summary>
    Accepted = 3,

    /// <summary>The server reported a concurrent change. Needs an explicit decision by an authorized user.</summary>
    Conflict = 4,

    /// <summary>The server rejected the change permanently (validation). Needs attention; never silently dropped.</summary>
    Rejected = 5,

    /// <summary>The user lost permission for this change. Kept for review.</summary>
    Forbidden = 6,

    /// <summary>Replaced by a newer entry after the user chose to keep their version in a conflict.</summary>
    Superseded = 7,

    /// <summary>The user explicitly chose the server version in a conflict; the local version is kept for audit.</summary>
    Discarded = 8,
}

/// <summary>
/// Durable record of a local change awaiting upload (transactional outbox). Written in the same SQLite
/// transaction as the change itself, so a saved change can never be lost before it is uploaded.
/// </summary>
public sealed class OutboxEntry
{
    /// <summary>Device-local upload order (primary key, assigned in dependency-safe order).</summary>
    public long LocalSequence { get; set; }

    /// <summary>Idempotency key sent to the server. Never changes once the entry has been sent.</summary>
    public Guid ChangeId { get; set; }

    public Guid CompanyId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public SyncOperation Operation { get; set; }

    /// <summary>Server version the change was based on; 0 for rows created on this device.</summary>
    public long BaseVersion { get; set; }

    public int SchemaVersion { get; set; } = SyncProtocol.SchemaVersion;

    public string PayloadJson { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public DateTime ClientModifiedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    public int Attempts { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastError { get; set; }

    public long? AcceptedVersion { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public Guid? ConflictId { get; set; }

    public Guid? ResolvesConflictId { get; set; }
}

/// <summary>Download position per company: the last server change sequence applied locally.</summary>
public sealed class SyncCursor
{
    public Guid CompanyId { get; set; }

    public long Cursor { get; set; }

    public DateTime? LastPulledAtUtc { get; set; }

    /// <summary>The server refused access (membership revoked). Local data is kept but hidden.</summary>
    public bool AccessRevoked { get; set; }
}

public enum LocalConflictStatus : byte
{
    Open = 1,
    KeptServer = 2,
    KeptLocal = 3,
}

/// <summary>A change the server did not accept, with both versions, awaiting an explicit decision.</summary>
public sealed class LocalConflict
{
    public Guid Id { get; set; }

    public long OutboxSequence { get; set; }

    public Guid? ServerConflictId { get; set; }

    public Guid CompanyId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }

    public string LocalPayloadJson { get; set; } = string.Empty;

    public string? ServerPayloadJson { get; set; }

    public long? ServerVersion { get; set; }

    public DateTime DetectedAtUtc { get; set; }

    public LocalConflictStatus Status { get; set; } = LocalConflictStatus.Open;

    public DateTime? ResolvedAtUtc { get; set; }

    public Guid? ResolvedByUserId { get; set; }
}

/// <summary>Identity of this installation (single row, Id = 1).</summary>
public sealed class DeviceRegistration
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Generated locally on first start and registered with the server during provisioning.</summary>
    public Guid DeviceId { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Voucher-number series assigned by the server at registration; null until provisioned.</summary>
    public string? SeriesCode { get; set; }

    public string? ServerUrl { get; set; }

    public DateTime? RegisteredAtUtc { get; set; }

    public DateTime? LastSuccessfulSyncAtUtc { get; set; }

    public bool IsRevoked { get; set; }
}

/// <summary>
/// Offline sign-in data for a user provisioned on this device. Holds a PBKDF2 verifier of the password (never the
/// password), additionally protected with the OS data-protection API where available.
/// </summary>
public sealed class LocalUser
{
    public Guid UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public byte[] VerifierSalt { get; set; } = [];

    public byte[] ProtectedVerifier { get; set; } = [];

    public int Iterations { get; set; }

    public int OfflineGraceDays { get; set; }

    public DateTime LastOnlineAuthAtUtc { get; set; }

    public DateTime OfflineAllowedUntilUtc { get; set; }

    public int FailedAttempts { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>Set when the server reports the account or device revoked; offline sign-in is then refused.</summary>
    public bool IsRevoked { get; set; }
}

/// <summary>A secret (e.g. refresh token) encrypted with the OS data-protection API, keyed by purpose.</summary>
public sealed class ProtectedSecret
{
    public string Key { get; set; } = string.Empty;

    public byte[] ProtectedValue { get; set; } = [];

    public DateTime UpdatedAtUtc { get; set; }
}
