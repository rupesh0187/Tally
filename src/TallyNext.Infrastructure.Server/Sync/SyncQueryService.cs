using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Companies;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Sync;

/// <summary>Download side of sync and conflict review. Every call is scoped to companies the caller belongs to.</summary>
public sealed class SyncQueryService(ServerDbContext db, IAccessGuard guard, IAuditTrail audit, IExecutionContext context, TimeProvider clock)
{
    /// <summary>Companies this user may synchronize, with the latest change sequence of each.</summary>
    public async Task<IReadOnlyList<SyncCompanyInfo>> CompaniesAsync(CancellationToken cancellationToken = default)
    {
        var userId = context.RequireUserId();
        return await (
                from m in db.CompanyMemberships.AsNoTracking()
                join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
                join s in db.CompanySyncStates.AsNoTracking() on c.Id equals s.CompanyId into states
                from s in states.DefaultIfEmpty()
                where m.UserId == userId && m.IsActive && !m.IsDeleted && !c.IsDeleted
                orderby c.Name
                select new SyncCompanyInfo(c.Id, c.Name, m.Role, s == null ? 0 : s.LastSequence))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Change-log entries after <paramref name="cursor"/> in sequence order. Cursor 0 replays the full history, which
    /// is the initial download for a new device.
    /// </summary>
    public async Task<SyncPullResponse> PullAsync(Guid companyId, long cursor, int limit, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        if (cursor < 0)
        {
            throw new ValidationFailedException("sync.cursor_invalid", "The cursor must not be negative.");
        }

        limit = limit <= 0 ? SyncProtocol.DefaultPullBatchSize : Math.Min(limit, SyncProtocol.MaxPullBatchSize);
        var rows = await db.SyncChanges.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.Sequence > cursor)
            .OrderBy(c => c.Sequence)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;
        var changes = page.Select(r => new ServerChange(
                r.Sequence, r.EntityType, r.EntityId, r.Operation, r.Version, r.OriginDeviceId, r.OriginChangeId, r.ChangedAtUtc,
                Parse(r.PayloadJson)))
            .ToList();
        var next = page.Count > 0 ? page[^1].Sequence : cursor;
        return new SyncPullResponse(companyId, cursor, next, hasMore, SyncProtocol.SchemaVersion, changes);
    }

    /// <summary>Current server state of one row of a company the caller belongs to.</summary>
    public async Task<ServerEntityState> GetEntityAsync(Guid companyId, string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        Domain.Common.ISyncEntity? entity = entityType switch
        {
            SyncEntityTypes.Company => await db.Companies.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.Id == companyId, cancellationToken),
            SyncEntityTypes.AccountGroup => await db.AccountGroups.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.Ledger => await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.VoucherType => await db.VoucherTypes.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.Voucher => await db.Vouchers.AsNoTracking().Include(v => v.Lines).Include(v => v.ItemLines).SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.Unit => await db.Units.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.Godown => await db.Godowns.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.StockItem => await db.StockItems.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            SyncEntityTypes.Membership => await db.CompanyMemberships.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entityId && e.CompanyId == companyId, cancellationToken),
            _ => throw new ValidationFailedException("sync.entity_type_unknown", $"Unknown entity type '{entityType}'."),
        };

        return entity is null
            ? new ServerEntityState(entityType, entityId, false, 0, null)
            : new ServerEntityState(entityType, entityId, true, entity.Version, SyncJson.ToElement(Application.Sync.SnapshotMapper.ToSnapshot(entity)));
    }

    public async Task<IReadOnlyList<SyncConflictDto>> ConflictsAsync(Guid companyId, SyncConflictStatus? status, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.SyncConflictResolve, cancellationToken);
        var query = db.SyncConflicts.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        return await query.OrderByDescending(c => c.CreatedAtUtc).Take(500).Select(c => ToDto(c)).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Records a reviewer's decision about a conflict. Data changes are made by the device (keep server = re-download,
    /// keep client = re-upload with the server version as base); this records the decision for the audit trail.
    /// </summary>
    public async Task<SyncConflictDto> ResolveConflictAsync(Guid companyId, Guid conflictId, ResolveConflictRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.SyncConflictResolve, cancellationToken);
        if (request.Resolution == SyncConflictStatus.Open || !Enum.IsDefined(request.Resolution))
        {
            throw new ValidationFailedException("sync.resolution_invalid", "Choose how the conflict was resolved.");
        }

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var conflict = await db.SyncConflicts.SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Id == conflictId, cancellationToken)
            ?? throw new NotFoundException("conflict");
        conflict.Status = request.Resolution;
        conflict.ResolvedByUserId = context.RequireUserId();
        conflict.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
        conflict.ResolutionNote = request.Note is { Length: > 1000 } note ? note[..1000] : request.Note;
        audit.Record(companyId, "sync.conflict_resolved", conflict.EntityType, conflict.EntityId, $"Conflict {conflict.Id} marked {request.Resolution}", changeId: conflict.ChangeId);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(conflict);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SyncConflictDto ToDto(SyncConflictRecord c) => new(
        c.Id, c.CompanyId, c.EntityType, c.EntityId, c.DeviceId, c.ChangeId, c.UserId, c.BaseVersion, c.ServerVersion, c.Reason,
        c.ClientPayloadJson, c.ServerPayloadJson, c.CreatedAtUtc, c.Status, c.ResolvedByUserId, c.ResolvedAtUtc, c.ResolutionNote);
}
