using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Persistence;

namespace TallyNext.Infrastructure.Local.Sync;

public sealed record LocalConflictDto(
    Guid Id,
    Guid CompanyId,
    string EntityType,
    Guid EntityId,
    string Kind,
    string? ErrorCode,
    string? Message,
    string LocalPayloadJson,
    string? ServerPayloadJson,
    long? ServerVersion,
    DateTime DetectedAtUtc,
    LocalConflictStatus Status);

public sealed record PendingChangeDto(long LocalSequence, string EntityType, Guid EntityId, SyncOperation Operation, OutboxStatus Status, int Attempts, string? LastErrorCode, DateTime ClientModifiedAtUtc);

/// <summary>
/// Explicit, audited decisions about changes the server did not accept. Nothing is ever discarded silently: the
/// local version remains in the outbox (status Discarded or Superseded) and the decision is in the audit trail.
/// </summary>
public sealed class ConflictResolver(
    LocalDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    IExecutionContext context,
    ISyncTransport transport,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<LocalConflictDto>> ListAsync(Guid companyId, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        var query = db.LocalConflicts.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (openOnly)
        {
            query = query.Where(c => c.Status == LocalConflictStatus.Open);
        }

        return await query
            .OrderByDescending(c => c.DetectedAtUtc)
            .Select(c => new LocalConflictDto(c.Id, c.CompanyId, c.EntityType, c.EntityId, c.Kind, c.ErrorCode, c.Message, c.LocalPayloadJson, c.ServerPayloadJson, c.ServerVersion, c.DetectedAtUtc, c.Status))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Local changes of the company not yet accepted by the server (for the sync status screen).</summary>
    public async Task<IReadOnlyList<PendingChangeDto>> ListUnsyncedAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        OutboxStatus[] open = [OutboxStatus.Pending, OutboxStatus.InFlight, OutboxStatus.Conflict, OutboxStatus.Rejected, OutboxStatus.Forbidden];
        return await db.OutboxEntries.AsNoTracking()
            .Where(e => e.CompanyId == companyId && open.Contains(e.Status))
            .OrderBy(e => e.LocalSequence)
            .Take(500)
            .Select(e => new PendingChangeDto(e.LocalSequence, e.EntityType, e.EntityId, e.Operation, e.Status, e.Attempts, e.LastErrorCode, e.ClientModifiedAtUtc))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Accept the server's version: the local row is overwritten with the server state (fetched if needed), and the
    /// local change plus any later edits of the same row are marked Discarded (kept for review).
    /// A row that never reached the server is tombstoned locally.
    /// </summary>
    public async Task KeepServerVersionAsync(Guid companyId, Guid conflictId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.SyncConflictResolve, cancellationToken);
        var conflict = await LoadOpenAsync(companyId, conflictId, cancellationToken);

        long version;
        object? snapshot = null;
        if (conflict.ServerPayloadJson is not null && conflict.ServerVersion is { } knownVersion)
        {
            version = knownVersion;
            snapshot = System.Text.Json.JsonSerializer.Deserialize(conflict.ServerPayloadJson, SyncJson.SnapshotTypeOf(conflict.EntityType), SyncJson.Options);
        }
        else
        {
            var state = await transport.GetEntityAsync(companyId, conflict.EntityType, conflict.EntityId, cancellationToken);
            version = state.Version;
            if (state is { Exists: true, Payload: { } payload })
            {
                snapshot = System.Text.Json.JsonSerializer.Deserialize(payload, SyncJson.SnapshotTypeOf(conflict.EntityType), SyncJson.Options);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", cancellationToken);
        using (db.BeginRemoteApply())
        {
            if (snapshot is not null)
            {
                await SnapshotApplier.UpsertAsync(db, snapshot, version, cancellationToken);
            }
            else
            {
                await TombstoneLocalOnlyAsync(conflict.EntityType, conflict.EntityId, cancellationToken);
            }

            await MarkEntriesAsync(conflict.EntityId, OutboxStatus.Discarded, cancellationToken);
            Close(conflict, LocalConflictStatus.KeptServer);
            await db.SaveChangesAsync(cancellationToken);
        }

        audit.Record(companyId, "sync.conflict_resolved", conflict.EntityType, conflict.EntityId, $"Kept the server version ({conflict.Kind}: {conflict.ErrorCode})");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Keep (or retry) the local version: the row's current local state is queued again, based on the server version
    /// for conflicts, so the server applies it on top of the other change. Earlier queued entries become Superseded.
    /// </summary>
    public async Task KeepLocalVersionAsync(Guid companyId, Guid conflictId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.SyncConflictResolve, cancellationToken);
        var userId = context.RequireUserId();
        var conflict = await LoadOpenAsync(companyId, conflictId, cancellationToken);
        var entry = await db.OutboxEntries.SingleAsync(e => e.LocalSequence == conflict.OutboxSequence, cancellationToken);
        var entity = await LoadEntityAsync(conflict.EntityType, conflict.EntityId, cancellationToken)
            ?? throw new NotFoundException("record");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var baseVersion = conflict.Kind == nameof(SyncOutcome.Conflict) && conflict.ServerVersion is { } serverVersion ? serverVersion : entry.BaseVersion;
        await MarkEntriesAsync(conflict.EntityId, OutboxStatus.Superseded, cancellationToken);

        var next = (await db.OutboxEntries.MaxAsync(e => (long?)e.LocalSequence, cancellationToken) ?? 0) + 1;
        var now = clock.GetUtcNow().UtcDateTime;
        db.OutboxEntries.Add(new OutboxEntry
        {
            LocalSequence = next,
            ChangeId = Ids.New(),
            CompanyId = companyId,
            EntityType = conflict.EntityType,
            EntityId = conflict.EntityId,
            Operation = entity.IsDeleted ? SyncOperation.Delete : SyncOperation.Upsert,
            BaseVersion = baseVersion,
            SchemaVersion = SyncProtocol.SchemaVersion,
            PayloadJson = SyncJson.Serialize(SnapshotMapper.ToSnapshot(entity)),
            UserId = userId,
            ClientModifiedAtUtc = now,
            CreatedAtUtc = now,
            Status = OutboxStatus.Pending,
            ResolvesConflictId = conflict.ServerConflictId,
        });
        Close(conflict, LocalConflictStatus.KeptLocal);
        audit.Record(companyId, "sync.conflict_resolved", conflict.EntityType, conflict.EntityId, $"Kept the local version ({conflict.Kind}: {conflict.ErrorCode}); queued for upload again");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<LocalConflict> LoadOpenAsync(Guid companyId, Guid conflictId, CancellationToken cancellationToken) =>
        await db.LocalConflicts.SingleOrDefaultAsync(c => c.Id == conflictId && c.CompanyId == companyId && c.Status == LocalConflictStatus.Open, cancellationToken)
        ?? throw new NotFoundException("conflict");

    private void Close(LocalConflict conflict, LocalConflictStatus status)
    {
        conflict.Status = status;
        conflict.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
        conflict.ResolvedByUserId = context.UserId;
    }

    private async Task MarkEntriesAsync(Guid entityId, OutboxStatus status, CancellationToken cancellationToken)
    {
        OutboxStatus[] open = [OutboxStatus.Pending, OutboxStatus.InFlight, OutboxStatus.Conflict, OutboxStatus.Rejected, OutboxStatus.Forbidden];
        var entries = await db.OutboxEntries.Where(e => e.EntityId == entityId && open.Contains(e.Status)).ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            entry.Status = status;
            entry.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        // Other open conflicts about the same row are settled by the same decision.
        var others = await db.LocalConflicts.Where(c => c.EntityId == entityId && c.Status == LocalConflictStatus.Open).ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            Close(other, status == OutboxStatus.Discarded ? LocalConflictStatus.KeptServer : LocalConflictStatus.KeptLocal);
        }
    }

    private async Task TombstoneLocalOnlyAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var entity = await LoadEntityAsync(entityType, entityId, cancellationToken);
        if (entity is null || entity.IsDeleted)
        {
            return;
        }

        var referenced = entityType switch
        {
            SyncEntityTypes.Ledger => await db.VoucherLines.AnyAsync(l => l.LedgerId == entityId && db.Vouchers.Any(v => v.Id == l.VoucherId && !v.IsDeleted), cancellationToken)
                                      || await db.VoucherItemLines.AnyAsync(l => l.LedgerId == entityId && db.Vouchers.Any(v => v.Id == l.VoucherId && !v.IsDeleted), cancellationToken)
                                      || await db.Vouchers.AnyAsync(v => v.PartyLedgerId == entityId && !v.IsDeleted, cancellationToken),
            SyncEntityTypes.AccountGroup => await db.Ledgers.AnyAsync(l => l.GroupId == entityId && !l.IsDeleted, cancellationToken)
                                            || await db.AccountGroups.AnyAsync(g => g.ParentGroupId == entityId && !g.IsDeleted, cancellationToken),
            SyncEntityTypes.VoucherType => await db.Vouchers.AnyAsync(v => v.VoucherTypeId == entityId && !v.IsDeleted, cancellationToken),
            SyncEntityTypes.Voucher => await db.Vouchers.AnyAsync(v => v.ReversalOfVoucherId == entityId && !v.IsDeleted, cancellationToken),
            SyncEntityTypes.Unit => await db.StockItems.AnyAsync(i => i.UnitId == entityId && !i.IsDeleted, cancellationToken),
            SyncEntityTypes.Godown => await db.StockItems.AnyAsync(i => i.OpeningGodownId == entityId && !i.IsDeleted, cancellationToken)
                                      || await db.VoucherItemLines.AnyAsync(l => l.GodownId == entityId && db.Vouchers.Any(v => v.Id == l.VoucherId && !v.IsDeleted), cancellationToken),
            SyncEntityTypes.StockItem => await db.VoucherItemLines.AnyAsync(l => l.StockItemId == entityId && db.Vouchers.Any(v => v.Id == l.VoucherId && !v.IsDeleted), cancellationToken),
            _ => true,
        };
        if (referenced)
        {
            throw new ConflictException("sync.discard_blocked", "Other local records still use this record. Resolve those first.");
        }

        db.Entry(entity).Property(nameof(ISyncEntity.IsDeleted)).CurrentValue = true;
        if (entity is Domain.Vouchers.Voucher voucher)
        {
            foreach (var line in voucher.Lines)
            {
                db.Entry(line).Property(nameof(Domain.Vouchers.VoucherLine.IsPosted)).CurrentValue = false;
            }

            foreach (var line in voucher.ItemLines)
            {
                db.Entry(line).Property(nameof(Domain.Vouchers.VoucherItemLine.IsPosted)).CurrentValue = false;
            }
        }
    }

    private async Task<ISyncEntity?> LoadEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken) => entityType switch
    {
        SyncEntityTypes.Company => await db.Companies.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.AccountGroup => await db.AccountGroups.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.Ledger => await db.Ledgers.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.VoucherType => await db.VoucherTypes.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.Voucher => await db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines).SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.Unit => await db.Units.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.Godown => await db.Godowns.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        SyncEntityTypes.StockItem => await db.StockItems.SingleOrDefaultAsync(e => e.Id == entityId, cancellationToken),
        _ => null,
    };
}
