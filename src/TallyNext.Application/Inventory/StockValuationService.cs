using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Inventory;

public sealed record ItemValuation(Guid StockItemId, StockPeriodResult Result, IReadOnlyDictionary<Guid, long> ClosingQuantityByGodown);

/// <summary>Stock values for reports (no permission checks: callers are report use cases that already checked).</summary>
public sealed record StockTotals(long OpeningAtFromMinor, long ClosingAtToMinor, long BooksOpeningMinor)
{
    public static StockTotals None { get; } = new(0, 0, 0);
}

/// <summary>
/// Values inventory from item openings (as at books beginning) and posted invoice lines, in date order. Purchases
/// bring stock in at their taxable value; sales returns come back at cost; everything outward goes at cost
/// (weighted average or FIFO per item). Values are recomputed from the authoritative lines on every request.
/// </summary>
public sealed class StockValuationService(IAccountingDbContext db)
{
    private sealed record Movement(Guid StockItemId, Guid? GodownId, DateOnly Date, Guid VoucherId, int LineNo, StockDirection Direction, long QuantityMilli, long TaxableMinor, VoucherBaseType BaseType, bool IsReversal);

    public async Task<IReadOnlyList<ItemValuation>> ValueAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .Select(i => new { i.Id, i.ValuationMethod, i.OpeningQuantityMilli, i.OpeningValueMinor, i.OpeningGodownId })
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return [];
        }

        var movements = await (
                from l in db.VoucherItemLines.AsNoTracking()
                join v in db.Vouchers.AsNoTracking() on l.VoucherId equals v.Id
                where l.CompanyId == companyId && l.IsPosted && l.StockItemId != null && l.VoucherDate <= to && l.Direction != StockDirection.None
                orderby l.VoucherDate, l.VoucherId, l.LineNo
                select new Movement(l.StockItemId!.Value, l.GodownId, l.VoucherDate, l.VoucherId, l.LineNo, l.Direction, l.QuantityMilli, l.TaxableMinor, v.BaseType, v.ReversalOfVoucherId != null))
            .ToListAsync(cancellationToken);
        var byItem = movements.ToLookup(m => m.StockItemId);

        var result = new List<ItemValuation>(items.Count);
        foreach (var item in items)
        {
            var itemMovements = byItem[item.Id].ToList();
            var flow = itemMovements.Select(m => new StockMovement(
                m.Date,
                m.Direction,
                m.QuantityMilli,
                m.Direction == StockDirection.In && m.BaseType == VoucherBaseType.Purchase && !m.IsReversal ? m.TaxableMinor : null));
            var valuation = StockValuation.Run(item.ValuationMethod, new StockPosition(item.OpeningQuantityMilli, item.OpeningValueMinor), flow, from, to);

            var byGodown = new Dictionary<Guid, long>();
            if (item.OpeningGodownId is { } openingGodown && item.OpeningQuantityMilli != 0)
            {
                byGodown[openingGodown] = item.OpeningQuantityMilli;
            }

            foreach (var m in itemMovements.Where(m => m.GodownId is not null))
            {
                var signed = m.Direction == StockDirection.In ? m.QuantityMilli : -m.QuantityMilli;
                byGodown[m.GodownId!.Value] = byGodown.GetValueOrDefault(m.GodownId.Value) + signed;
            }

            result.Add(new ItemValuation(item.Id, valuation, byGodown));
        }

        return result;
    }

    /// <summary>Total stock value at the start of <paramref name="from"/>, at the end of <paramref name="to"/>, and at books beginning.</summary>
    public async Task<StockTotals> TotalsAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var valuations = await ValueAsync(companyId, from, to, cancellationToken);
        if (valuations.Count == 0)
        {
            return StockTotals.None;
        }

        var booksOpening = await db.StockItems.AsNoTracking()
            .Where(i => i.CompanyId == companyId && !i.IsDeleted)
            .SumAsync(i => i.OpeningValueMinor, cancellationToken);
        return new StockTotals(
            valuations.Sum(v => v.Result.Opening.ValueMinor),
            valuations.Sum(v => v.Result.Closing.ValueMinor),
            booksOpening);
    }
}
