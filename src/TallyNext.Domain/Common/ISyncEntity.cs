namespace TallyNext.Domain.Common;

/// <summary>
/// An aggregate root that is replicated between the central server and offline devices.
/// </summary>
public interface ISyncEntity
{
    Guid Id { get; }

    /// <summary>Company (tenant) that owns the row. Every read and write is scoped by it.</summary>
    Guid CompanyId { get; }

    /// <summary>
    /// Server-issued version: the per-company change sequence number of the last change the server
    /// accepted for this row. 0 means the row has never been accepted by the server (created offline).
    /// Used as the optimistic-concurrency token and as the "base version" when a device uploads an edit.
    /// </summary>
    long Version { get; }

    /// <summary>Tombstone flag. Deleted rows are kept so that deletions replicate and cannot be resurrected.</summary>
    bool IsDeleted { get; }
}

/// <summary>Base class for company-owned replicated aggregates.</summary>
public abstract class CompanyScopedEntity : ISyncEntity
{
    public Guid Id { get; protected set; }
    public Guid CompanyId { get; protected set; }
    public long Version { get; private set; }
    public bool IsDeleted { get; protected set; }

    /// <summary>Stamped by persistence (store time), not by the client clock.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Stamped by persistence (store time), not by the client clock.</summary>
    public DateTime UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }
}
