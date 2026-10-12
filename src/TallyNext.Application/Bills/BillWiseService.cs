using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Contracts.Reports;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Bills;

/// <summary>
/// Bill-wise outstanding: pending bills per party with ageing, and the on-account part of each party balance.
/// Computed on request from opening bills and posted allocations (nothing stored, so it cannot drift).
/// </summary>
/// <remarks>
/// A bill is every allocation with the same (ledger, case-insensitive name). Its date, due date and display name come
/// from its first opening bill, new reference or advance; its pending amount is the signed sum of all its allocations.
/// The on-account amount of a party is its ledger balance minus its pending bills, so amounts posted without a
/// breakup (older vouchers, unallocated remainders) are never lost.
/// </remarks>
public sealed class BillWiseService(IAccountingDbContext db, IAccessGuard guard)
{
    private static readonly (string Label, int From, int To)[] Buckets =
    [
        ("Not yet due", int.MinValue, -1),
        ("0-30 days", 0, 30),
        ("31-60 days", 31, 60),
        ("61-90 days", 61, 90),
        ("Over 90 days", 91, int.MaxValue),
    ];

    private sealed record Entry(Guid LedgerId, string Name, string Normalized, BillAllocationKind? Kind, DateOnly Date, DateOnly? DueDate, long SignedMinor, Guid VoucherId, int LineNo, int AllocationNo);

    private sealed record BillState(Guid LedgerId, string Name, DateOnly BillDate, DateOnly? DueDate, long OriginalSigned, long PendingSigned, bool IsAdvance);

    /// <summary>Bills of one ledger with a pending amount (for choosing what a receipt or payment settles).</summary>
    public async Task<IReadOnlyList<PendingBillDto>> PendingBillsAsync(Guid companyId, Guid ledgerId, DateOnly? asAt = null, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.VoucherView, cancellationToken);
        var bills = await ComputeBillsAsync(companyId, [ledgerId], asAt ?? DateOnly.MaxValue, cancellationToken);
        return bills
            .Where(b => b.PendingSigned != 0)
            .OrderBy(b => b.DueDate ?? b.BillDate).ThenBy(b => b.BillDate).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new PendingBillDto(b.Name, b.BillDate, b.DueDate, Amounts.ToDecimal(Math.Abs(b.PendingSigned)), b.PendingSigned > 0 ? DrCr.Debit : DrCr.Credit))
            .ToList();
    }

    /// <summary>Receivables (bill-wise ledgers under asset groups) or payables (under liability groups) as at a date.</summary>
    public async Task<OutstandingReport> OutstandingAsync(
        Guid companyId, OutstandingKind kind, DateOnly asAt, Guid? ledgerId = null, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.ReportView, cancellationToken);
        if (!Enum.IsDefined(kind))
        {
            throw new ValidationFailedException("outstanding.kind_invalid", "Choose receivables or payables.");
        }

        var groups = await db.AccountGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync(cancellationToken);
        var hierarchy = new GroupHierarchy(groups);
        var nature = kind == OutstandingKind.Receivables ? AccountNature.Asset : AccountNature.Liability;
        var ledgers = (await db.Ledgers.AsNoTracking()
                .Where(l => l.CompanyId == companyId && !l.IsDeleted && l.IsBillWise && (ledgerId == null || l.Id == ledgerId))
                .Select(l => new { l.Id, l.Name, l.GroupId, l.OpeningBalanceMinor, l.OpeningSide })
                .ToListAsync(cancellationToken))
            .Where(l => hierarchy.Get(l.GroupId).Nature == nature)
            .ToList();
        var ids = ledgers.Select(l => l.Id).ToList();

        var bills = await ComputeBillsAsync(companyId, ids, asAt, cancellationToken);
        var movements = await db.VoucherLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.IsPosted && l.VoucherDate <= asAt && ids.Contains(l.LedgerId))
            .GroupBy(l => l.LedgerId)
            .Select(g => new { LedgerId = g.Key, Signed = g.Sum(l => l.Side == DrCr.Debit ? l.AmountMinor : -l.AmountMinor) })
            .ToDictionaryAsync(x => x.LedgerId, x => x.Signed, cancellationToken);

        // Report direction: receivables are debit balances, payables credit balances.
        var sign = kind == OutstandingKind.Receivables ? 1 : -1;
        var names = ledgers.ToDictionary(l => l.Id, l => l.Name);
        var outstandingBills = bills
            .Where(b => b.PendingSigned != 0)
            .Select(b =>
            {
                var reference = b.DueDate ?? b.BillDate;
                return new OutstandingBill(
                    b.LedgerId, names[b.LedgerId], b.Name, b.BillDate, b.DueDate,
                    Amounts.ToDecimal(sign * b.OriginalSigned), Amounts.ToDecimal(sign * b.PendingSigned),
                    asAt.DayNumber - reference.DayNumber, b.IsAdvance);
            })
            .OrderBy(b => b.LedgerName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.DueDate ?? b.BillDate).ThenBy(b => b.BillName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pendingByLedger = bills.GroupBy(b => b.LedgerId).ToDictionary(g => g.Key, g => g.Sum(b => b.PendingSigned));
        var ledgerRows = new List<OutstandingLedger>();
        foreach (var l in ledgers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var balance = (l.OpeningSide == DrCr.Debit ? l.OpeningBalanceMinor : -l.OpeningBalanceMinor) + movements.GetValueOrDefault(l.Id);
            var pending = pendingByLedger.GetValueOrDefault(l.Id);
            if (balance == 0 && pending == 0)
            {
                continue;
            }

            ledgerRows.Add(new OutstandingLedger(
                l.Id, l.Name, hierarchy.Get(l.GroupId).Name,
                Amounts.ToDecimal(sign * pending), Amounts.ToDecimal(sign * (balance - pending)), Amounts.ToDecimal(sign * balance)));
        }

        var ageing = Buckets
            .Select(bucket => new AgeingBucket(
                bucket.Label,
                outstandingBills.Where(b => b.OverdueDays >= bucket.From && b.OverdueDays <= bucket.To).Sum(b => b.Pending)))
            .ToList();
        var totalPending = ledgerRows.Sum(l => l.BillsPending);
        var totalOnAccount = ledgerRows.Sum(l => l.OnAccount);
        return new OutstandingReport(asAt, kind, outstandingBills, ledgerRows, ageing, totalPending, totalOnAccount, totalPending + totalOnAccount);
    }

    private async Task<List<BillState>> ComputeBillsAsync(Guid companyId, IReadOnlyCollection<Guid> ledgerIds, DateOnly asAt, CancellationToken cancellationToken)
    {
        if (ledgerIds.Count == 0)
        {
            return [];
        }

        var allocations = await db.VoucherBillAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsPosted && a.VoucherDate <= asAt && ledgerIds.Contains(a.LedgerId) && a.NormalizedBillName != null)
            .Select(a => new { a.LedgerId, a.BillName, a.NormalizedBillName, a.Kind, a.VoucherDate, a.DueDate, a.Side, a.AmountMinor, a.VoucherId, a.LineNo, a.AllocationNo })
            .ToListAsync(cancellationToken);
        var openings = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && ledgerIds.Contains(l.Id))
            .Select(l => new { l.Id, l.OpeningBills })
            .ToListAsync(cancellationToken);

        var entries = openings
            .SelectMany(l => l.OpeningBills.Select(b => new Entry(
                l.Id, b.Name, BillNames.Normalize(b.Name), null, b.Date, b.DueDate, b.SignedAmountMinor, Guid.Empty, 0, 0)))
            .Concat(allocations.Select(a => new Entry(
                a.LedgerId, a.BillName!, a.NormalizedBillName!, a.Kind, a.VoucherDate, a.DueDate,
                a.Side == DrCr.Debit ? a.AmountMinor : -a.AmountMinor, a.VoucherId, a.LineNo, a.AllocationNo)))
            .ToList();

        var result = new List<BillState>();
        foreach (var group in entries.GroupBy(e => (e.LedgerId, e.Normalized)))
        {
            var ordered = group
                .OrderBy(e => e.Kind is null ? 0 : 1)
                .ThenBy(e => e.Date)
                .ThenBy(e => e.VoucherId)
                .ThenBy(e => e.LineNo)
                .ThenBy(e => e.AllocationNo)
                .ToList();
            var origins = ordered.Where(e => e.Kind is null or BillAllocationKind.NewRef or BillAllocationKind.Advance).ToList();
            if (origins.Count == 0)
            {
                // Settlements of a bill this store has not seen created (e.g. made offline elsewhere): still shown.
                origins = ordered;
            }

            var first = origins[0];
            result.Add(new BillState(
                group.Key.LedgerId,
                first.Name,
                first.Date,
                origins.Select(o => o.DueDate).FirstOrDefault(d => d is not null),
                origins.Sum(o => o.SignedMinor),
                ordered.Sum(e => e.SignedMinor),
                first.Kind == BillAllocationKind.Advance));
        }

        return result;
    }
}
