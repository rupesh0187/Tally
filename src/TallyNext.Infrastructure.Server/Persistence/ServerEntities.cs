using Microsoft.AspNetCore.Identity;
using TallyNext.Contracts.Sync;

namespace TallyNext.Infrastructure.Server.Persistence;

/// <summary>Account managed by ASP.NET Core Identity (password hash, lockout, security stamp).</summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }

    /// <summary>Disabled users cannot sign in and their existing tokens stop working at the next request.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A desktop installation. Its series code numbers the vouchers posted on it (globally unique).</summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string SeriesCode { get; set; } = string.Empty;

    public Guid RegisteredByUserId { get; set; }

    public DateTime RegisteredAtUtc { get; set; }

    public DateTime? LastSeenAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public Guid? RevokedByUserId { get; set; }

    public bool IsRevoked => RevokedAtUtc is not null;
}

/// <summary>
/// Opaque refresh token; only a SHA-256 hash is stored. Tokens rotate on every use; all tokens issued from one
/// login share a family so that reuse of a rotated token revokes the whole family.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? DeviceId { get; set; }

    public Guid FamilyId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;
}

/// <summary>Per-company write lock row and change sequence counter. See ARCHITECTURE.md, "Company write lock".</summary>
public sealed class CompanySyncState
{
    public Guid CompanyId { get; set; }

    public long LastSequence { get; set; }
}

/// <summary>Transactional server change log: one row per accepted change of a replicated entity.</summary>
public sealed class SyncChangeRecord
{
    public Guid CompanyId { get; set; }

    public long Sequence { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public SyncOperation Operation { get; set; }

    public long Version { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public Guid? OriginDeviceId { get; set; }

    public Guid? OriginChangeId { get; set; }

    public Guid? UserId { get; set; }

    public DateTime ChangedAtUtc { get; set; }
}

/// <summary>Idempotency ledger: the stored result of every processed (device, change id).</summary>
public sealed class ProcessedChange
{
    public Guid DeviceId { get; set; }

    public Guid ChangeId { get; set; }

    public Guid CompanyId { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public SyncOutcome Outcome { get; set; }

    public string ResultJson { get; set; } = string.Empty;

    public DateTime ProcessedAtUtc { get; set; }
}

/// <summary>A device change the server did not apply because the row changed concurrently.</summary>
public sealed class SyncConflictRecord
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid ChangeId { get; set; }

    public Guid UserId { get; set; }

    public long BaseVersion { get; set; }

    public long ServerVersion { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string ClientPayloadJson { get; set; } = string.Empty;

    public string? ServerPayloadJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public SyncConflictStatus Status { get; set; } = SyncConflictStatus.Open;

    public Guid? ResolvedByUserId { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolutionNote { get; set; }
}

/// <summary>Single-row counter for allocating device series codes.</summary>
public sealed class DeviceSeriesCounter
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public long LastValue { get; set; }
}
