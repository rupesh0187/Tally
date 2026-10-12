using Microsoft.EntityFrameworkCore;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Infrastructure.Persistence;

/// <summary>
/// Materializes replicated state received from the authoritative side onto entities, bypassing domain methods.
/// Used only for data that the sender has already validated (server snapshots applied on a device). Property
/// values are copied by name from the snapshot record (see Contracts/Sync/Snapshots.cs).
/// </summary>
public static class SnapshotApplier
{
    /// <summary>Inserts or overwrites the entity described by <paramref name="snapshot"/> and returns it.</summary>
    public static async Task<ISyncEntity> UpsertAsync(DbContext context, object snapshot, long version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot switch
        {
            CompanySnapshot s => await UpsertSimpleAsync<Company>(context, s.Id, s, version, cancellationToken),
            AccountGroupSnapshot s => await UpsertSimpleAsync<AccountGroup>(context, s.Id, s, version, cancellationToken),
            LedgerSnapshot s => await UpsertSimpleAsync<Ledger>(context, s.Id, s, version, cancellationToken),
            VoucherTypeSnapshot s => await UpsertSimpleAsync<VoucherType>(context, s.Id, s, version, cancellationToken),
            // Membership ids are deterministic per (company, user), so a device's offline row and the server's coincide.
            MembershipSnapshot s => await UpsertSimpleAsync<CompanyMembership>(context, s.Id, s, version, cancellationToken),
            VoucherSnapshot s => await UpsertVoucherAsync(context, s, version, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.GetType().Name, "Unknown snapshot type."),
        };
    }

    private static async Task<T> UpsertSimpleAsync<T>(DbContext context, Guid id, object snapshot, long version, CancellationToken cancellationToken)
        where T : class, ISyncEntity
    {
        var entity = await context.Set<T>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null)
        {
            entity = Create<T>();
            var detached = context.Entry(entity);
            detached.CurrentValues.SetValues(snapshot);
            context.Add(entity);
        }
        else
        {
            context.Entry(entity).CurrentValues.SetValues(snapshot);
        }

        SavePipeline.SetVersion(context.Entry(entity), version);
        return entity;
    }

    private static async Task<Voucher> UpsertVoucherAsync(DbContext context, VoucherSnapshot snapshot, long version, CancellationToken cancellationToken)
    {
        var voucher = await context.Set<Voucher>().Include(v => v.Lines).SingleOrDefaultAsync(v => v.Id == snapshot.Id, cancellationToken);
        var isNew = voucher is null;
        if (voucher is null)
        {
            voucher = Create<Voucher>();
            context.Entry(voucher).CurrentValues.SetValues(snapshot);
            context.Add(voucher);
        }
        else
        {
            context.Entry(voucher).CurrentValues.SetValues(snapshot);
        }

        SavePipeline.SetVersion(context.Entry(voucher), version);

        var existing = voucher.Lines.ToDictionary(l => l.Id);
        var incomingIds = snapshot.Lines.Select(l => l.Id).ToHashSet();
        if (!isNew)
        {
            foreach (var stale in existing.Values.Where(l => !incomingIds.Contains(l.Id)).ToList())
            {
                context.Remove(stale);
            }
        }

        var isPosted = snapshot.Status == VoucherStatus.Posted;
        foreach (var lineSnapshot in snapshot.Lines)
        {
            var line = existing.GetValueOrDefault(lineSnapshot.Id);
            var lineIsNew = line is null;
            line ??= Create<VoucherLine>();
            var entry = context.Entry(line);
            entry.CurrentValues.SetValues(lineSnapshot);
            entry.Property(nameof(VoucherLine.VoucherId)).CurrentValue = snapshot.Id;
            entry.Property(nameof(VoucherLine.CompanyId)).CurrentValue = snapshot.CompanyId;
            entry.Property(nameof(VoucherLine.VoucherDate)).CurrentValue = snapshot.Date;
            entry.Property(nameof(VoucherLine.IsPosted)).CurrentValue = isPosted;
            if (lineIsNew)
            {
                context.Add(line);
            }
        }

        return voucher;
    }

    private static T Create<T>()
        where T : class =>
        (T)(Activator.CreateInstance(typeof(T), nonPublic: true)
            ?? throw new InvalidOperationException($"Cannot create {typeof(T).Name}."));
}
