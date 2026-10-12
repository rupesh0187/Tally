using TallyNext.Domain.Common;

namespace TallyNext.Domain.Audit;

/// <summary>Where an audit record was produced.</summary>
public enum AuditSource : byte
{
    /// <summary>Written by the central server for a web/API action.</summary>
    Server = 1,

    /// <summary>Written on a desktop device for a local (possibly offline) action.</summary>
    Device = 2,

    /// <summary>Written by the server when it accepted (or rejected) a change uploaded by a device.</summary>
    Sync = 3,
}

/// <summary>
/// Append-only audit record. Each store (server, every device) keeps a per-company hash chain:
/// <c>Hash = SHA-256(PreviousHash || canonical fields)</c>, with <see cref="ChainSequence"/> strictly increasing.
/// Any edit, deletion or re-ordering of records breaks the chain and is detected by verification.
/// Sequence and hashes are assigned by the persistence layer inside the same transaction as the change.
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Null for events outside any company (sign-in, device registration).</summary>
    public Guid? CompanyId { get; private set; }

    /// <summary>Position in the chain of <see cref="CompanyId"/> (or the global chain when null). Assigned on save.</summary>
    public long ChainSequence { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public Guid? UserId { get; private set; }

    public Guid? DeviceId { get; private set; }

    public AuditSource Source { get; private set; }

    /// <summary>Dotted action name, e.g. <c>voucher.posted</c>, <c>ledger.updated</c>, <c>auth.login_failed</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    public string? Summary { get; private set; }

    /// <summary>JSON object of changed fields: <c>{"field": {"old": ..., "new": ...}}</c> or a full snapshot for creates.</summary>
    public string? ChangesJson { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Sync change identifier when the action was replicated from a device.</summary>
    public Guid? ChangeId { get; private set; }

    public string PreviousHash { get; private set; } = string.Empty;

    public string Hash { get; private set; } = string.Empty;

    public static AuditEntry Record(
        Guid? companyId,
        string action,
        string entityType,
        Guid? entityId,
        DateTime occurredAtUtc,
        Guid? userId,
        Guid? deviceId,
        AuditSource source,
        string? summary = null,
        string? changesJson = null,
        string? correlationId = null,
        Guid? changeId = null)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > 100)
        {
            throw new DomainException("audit.action_invalid", "An audit action name is required (max 100 characters).");
        }

        return new AuditEntry
        {
            Id = Ids.New(),
            CompanyId = companyId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            // Truncate to microseconds: PostgreSQL timestamptz stores microseconds, and the hash chain must
            // verify identically after a round trip through either database.
            OccurredAtUtc = new DateTime(occurredAtUtc.Ticks - (occurredAtUtc.Ticks % 10), DateTimeKind.Utc),
            UserId = userId,
            DeviceId = deviceId,
            Source = source,
            Summary = summary is { Length: > 500 } ? summary[..500] : summary,
            ChangesJson = changesJson,
            CorrelationId = correlationId is { Length: > 100 } ? correlationId[..100] : correlationId,
            ChangeId = changeId,
        };
    }

    /// <summary>Called by persistence exactly once, inside the transaction that saves the entry.</summary>
    public void Seal(long chainSequence, string previousHash, string hash)
    {
        if (!string.IsNullOrEmpty(Hash))
        {
            throw new InvalidOperationException("Audit entries are immutable once sealed.");
        }

        ChainSequence = chainSequence;
        PreviousHash = previousHash;
        Hash = hash;
    }
}
