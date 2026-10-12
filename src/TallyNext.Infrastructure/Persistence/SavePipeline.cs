using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TallyNext.Application.Common;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Infrastructure.Persistence;

/// <summary>A replicated aggregate that is about to be written, with the operation it represents.</summary>
public sealed record PendingSyncChange(ISyncEntity Entity, EntityEntry Entry, bool IsNew)
{
    public bool IsDeletion => Entity.IsDeleted;
}

/// <summary>Steps shared by the server and device <c>SaveChanges</c> pipelines.</summary>
public static class SavePipeline
{
    /// <summary>
    /// Returns the replicated aggregates with pending changes. A change to a voucher line or invoice item line marks
    /// its voucher as changed, because a voucher replicates as a whole (header, lines and item lines).
    /// </summary>
    public static IReadOnlyList<PendingSyncChange> CollectSyncChanges(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tracker = context.ChangeTracker;
        tracker.DetectChanges();

        var changedVoucherIds = tracker.Entries<VoucherLine>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.State == EntityState.Deleted ? (Guid)e.OriginalValues[nameof(VoucherLine.VoucherId)]! : e.Entity.VoucherId)
            .ToHashSet();
        changedVoucherIds.UnionWith(tracker.Entries<VoucherItemLine>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.State == EntityState.Deleted ? (Guid)e.OriginalValues[nameof(VoucherItemLine.VoucherId)]! : e.Entity.VoucherId));
        changedVoucherIds.UnionWith(tracker.Entries<VoucherBillAllocation>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.State == EntityState.Deleted ? (Guid)e.OriginalValues[nameof(VoucherBillAllocation.VoucherId)]! : e.Entity.VoucherId));

        var result = new List<PendingSyncChange>();
        foreach (var entry in tracker.Entries().ToList())
        {
            if (entry.Entity is not ISyncEntity entity)
            {
                continue;
            }

            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} rows are never physically deleted; mark them deleted (tombstone) instead.");
            }

            var touched = entry.State is EntityState.Added or EntityState.Modified
                          || (entity is Voucher && changedVoucherIds.Contains(entity.Id));
            if (!touched)
            {
                continue;
            }

            if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Modified;
            }

            result.Add(new PendingSyncChange(entity, entry, entry.State == EntityState.Added));
        }

        return Order(result);
    }

    /// <summary>
    /// Stamps CreatedAtUtc / UpdatedAtUtc / UpdatedByUserId on changed aggregates. Store time, never client time.
    /// </summary>
    public static void Stamp(IEnumerable<PendingSyncChange> changes, DateTime nowUtc, Guid? userId)
    {
        foreach (var change in changes)
        {
            var entry = change.Entry;
            if (change.IsNew)
            {
                entry.Property(nameof(CompanyScopedEntity.CreatedAtUtc)).CurrentValue = nowUtc;
            }

            entry.Property(nameof(CompanyScopedEntity.UpdatedAtUtc)).CurrentValue = nowUtc;
            entry.Property(nameof(CompanyScopedEntity.UpdatedByUserId)).CurrentValue = userId;
        }
    }

    public static void SetVersion(EntityEntry entry, long version) =>
        entry.Property(nameof(ISyncEntity.Version)).CurrentValue = version;

    /// <summary>
    /// Assigns chain sequence numbers and hashes to new audit entries, one chain per company. Must run inside the
    /// transaction that saves them. The chain head row is locked first (a no-op UPDATE takes a row lock on
    /// PostgreSQL; SQLite write transactions are already exclusive), so concurrent writers cannot fork a chain.
    /// </summary>
    public static async Task SealAuditEntriesAsync(DbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pending = context.ChangeTracker.Entries<AuditEntry>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrEmpty(e.Entity.Hash))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Audit entries must be sealed inside a transaction.");
        }

        foreach (var chain in pending.GroupBy(e => e.Entity.CompanyId ?? Guid.Empty).OrderBy(g => g.Key))
        {
            var key = chain.Key;
            var genesis = AuditHasher.GenesisHash;
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO \"AuditChainHeads\" (\"ChainKey\", \"LastSequence\", \"LastHash\") VALUES ({key}, {0L}, {genesis}) ON CONFLICT DO NOTHING",
                cancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"UPDATE \"AuditChainHeads\" SET \"LastSequence\" = \"LastSequence\" WHERE \"ChainKey\" = {key}",
                cancellationToken);

            var head = await context.Set<AuditChainHead>().AsNoTracking().SingleAsync(h => h.ChainKey == key, cancellationToken);
            var sequence = head.LastSequence;
            var previous = head.LastHash;
            foreach (var entry in chain.OrderBy(e => e.Entity.OccurredAtUtc).ThenBy(e => e.Entity.Id))
            {
                sequence++;
                var hash = AuditHasher.Compute(entry.Entity, sequence, previous);
                entry.Entity.Seal(sequence, previous, hash);
                entry.Property(AuditChainHead.ChainKeyProperty).CurrentValue = key;
                previous = hash;
            }

            await context.Database.ExecuteSqlAsync(
                $"UPDATE \"AuditChainHeads\" SET \"LastSequence\" = {sequence}, \"LastHash\" = {previous} WHERE \"ChainKey\" = {key}",
                cancellationToken);
        }
    }

    /// <summary>
    /// Dependency-safe order: a referenced row always receives a lower sequence than rows referencing it, so
    /// replaying changes in sequence order never violates a foreign key.
    /// </summary>
    private static List<PendingSyncChange> Order(List<PendingSyncChange> changes)
    {
        var groupDepth = new Dictionary<Guid, int>();
        var changedGroups = changes.Select(c => c.Entity).OfType<AccountGroup>().ToDictionary(g => g.Id);

        int DepthOf(AccountGroup group)
        {
            if (groupDepth.TryGetValue(group.Id, out var cached))
            {
                return cached;
            }

            var depth = 0;
            var current = group;
            var guard = 0;
            while (current.ParentGroupId is { } parentId && changedGroups.TryGetValue(parentId, out var parent) && guard++ < 64)
            {
                depth++;
                current = parent;
            }

            groupDepth[group.Id] = depth;
            return depth;
        }

        static int Rank(ISyncEntity e) => e switch
        {
            Company => 0,
            CompanyMembership => 1,
            VoucherType => 2,
            AccountGroup => 3,
            Ledger => 4,
            UnitOfMeasure => 5,
            Godown => 6,
            StockItem => 7,
            Voucher v when v.ReversalOfVoucherId is null => 8,
            Voucher => 9,
            _ => 10,
        };

        return changes
            .OrderBy(c => Rank(c.Entity))
            .ThenBy(c => c.Entity is AccountGroup g ? DepthOf(g) : 0)
            .ThenBy(c => c.Entity.Id)
            .ToList();
    }
}
