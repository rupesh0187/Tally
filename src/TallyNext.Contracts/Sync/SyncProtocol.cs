using System.Text.Json;
using TallyNext.Domain.Companies;

namespace TallyNext.Contracts.Sync;

/// <summary>Constants of the device-server replication protocol. See SYNC_DESIGN.md.</summary>
public static class SyncProtocol
{
    /// <summary>Wire protocol version. The server rejects pushes/pulls with an unsupported version (HTTP 426).</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Version of the entity snapshot schemas carried in payloads.</summary>
    public const int SchemaVersion = 1;

    public const int MaxPushBatchSize = 200;

    public const int MaxPullBatchSize = 500;

    public const int DefaultPullBatchSize = 200;
}

public static class SyncEntityTypes
{
    public const string Company = "company";
    public const string AccountGroup = "account-group";
    public const string Ledger = "ledger";
    public const string VoucherType = "voucher-type";
    public const string Voucher = "voucher";
    public const string Membership = "membership";
    public const string Unit = "unit";
    public const string Godown = "godown";
    public const string StockItem = "stock-item";

    /// <summary>Entity types devices may upload. Memberships are server-authored only (download-only).</summary>
    public static IReadOnlySet<string> Uploadable { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Company, AccountGroup, Ledger, VoucherType, Voucher, Unit, Godown, StockItem };

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Company, AccountGroup, Ledger, VoucherType, Voucher, Membership, Unit, Godown, StockItem };
}

/// <summary>State-based replication: an upsert carries the full new state; a delete is a tombstone.</summary>
public enum SyncOperation : byte
{
    Upsert = 1,
    Delete = 2,
}

/// <param name="ChangeId">Unique id of this change (idempotency key). Retries resend the same id and identical payload.</param>
/// <param name="LocalSequence">Device-local monotonic order; the server applies a batch in this order.</param>
/// <param name="BaseVersion">Server version the edit was based on (0 for a record created on the device).</param>
/// <param name="ResolvesConflictId">Set when the user explicitly chose to keep their version after a conflict.</param>
public sealed record SyncChangeUpload(
    Guid ChangeId,
    long LocalSequence,
    Guid CompanyId,
    string EntityType,
    Guid EntityId,
    SyncOperation Operation,
    long BaseVersion,
    int SchemaVersion,
    DateTime ClientModifiedAtUtc,
    JsonElement Payload,
    Guid? ResolvesConflictId = null);

public sealed record SyncPushRequest(Guid DeviceId, int ProtocolVersion, IReadOnlyList<SyncChangeUpload> Changes);

public enum SyncOutcome : byte
{
    /// <summary>Applied. <see cref="SyncChangeResult.NewVersion"/> is the new server version.</summary>
    Accepted = 1,

    /// <summary>This change id was already applied earlier (a retry). Treated exactly like Accepted.</summary>
    Duplicate = 2,

    /// <summary>The record changed on the server since BaseVersion. Nothing was applied; a conflict record exists.</summary>
    Conflict = 3,

    /// <summary>Permanently invalid (validation or business rule). Nothing was applied. Needs user attention.</summary>
    Rejected = 4,

    /// <summary>Temporarily not applicable (e.g. a referenced record has not arrived yet). Retry later.</summary>
    Deferred = 5,

    /// <summary>The user is not (or no longer) allowed to make this change.</summary>
    Forbidden = 6,
}

public sealed record SyncChangeResult(
    Guid ChangeId,
    SyncOutcome Outcome,
    long? NewVersion = null,
    string? ErrorCode = null,
    string? Message = null,
    Guid? ConflictId = null,
    long? ServerVersion = null,
    JsonElement? ServerPayload = null);

public sealed record SyncPushResponse(IReadOnlyList<SyncChangeResult> Results, DateTime ServerTimeUtc);

/// <summary>One entry of the server's per-company change log.</summary>
public sealed record ServerChange(
    long Sequence,
    string EntityType,
    Guid EntityId,
    SyncOperation Operation,
    long Version,
    Guid? OriginDeviceId,
    Guid? OriginChangeId,
    DateTime ChangedAtUtc,
    JsonElement Payload);

/// <param name="Cursor">The cursor the client sent.</param>
/// <param name="NextCursor">Cursor to persist after applying <see cref="Changes"/> (the last sequence returned).</param>
public sealed record SyncPullResponse(
    Guid CompanyId,
    long Cursor,
    long NextCursor,
    bool HasMore,
    int SchemaVersion,
    IReadOnlyList<ServerChange> Changes);

public sealed record SyncCompanyInfo(Guid CompanyId, string Name, CompanyRole Role, long LatestSequence);

/// <summary>The server's current state of one row (used to resolve a conflict in favour of the server).</summary>
public sealed record ServerEntityState(string EntityType, Guid EntityId, bool Exists, long Version, JsonElement? Payload);

public enum SyncConflictStatus : byte
{
    Open = 1,
    ResolvedKeepServer = 2,
    ResolvedKeepClient = 3,
    Dismissed = 4,
}

public sealed record SyncConflictDto(
    Guid Id,
    Guid CompanyId,
    string EntityType,
    Guid EntityId,
    Guid DeviceId,
    Guid ChangeId,
    Guid UserId,
    long BaseVersion,
    long ServerVersion,
    string Reason,
    string ClientPayloadJson,
    string? ServerPayloadJson,
    DateTime CreatedAtUtc,
    SyncConflictStatus Status,
    Guid? ResolvedByUserId,
    DateTime? ResolvedAtUtc,
    string? ResolutionNote);

public sealed class ResolveConflictRequest
{
    public SyncConflictStatus Resolution { get; set; } = SyncConflictStatus.ResolvedKeepServer;

    public string? Note { get; set; }
}
