using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Common;

namespace TallyNext.Application.Vouchers;

/// <summary>Narrow, batched lookups used by registers and reports (one query per page, never per row).</summary>
internal static class VoucherReadHelpers
{
    /// <summary>The ledger on line 1 of each voucher - the "particulars" column of registers and the Day Book.</summary>
    public static async Task<Dictionary<Guid, (string Name, DrCr Side)>> FirstLineParticularsAsync(
        IAccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> voucherIds, CancellationToken cancellationToken)
    {
        if (voucherIds.Count == 0)
        {
            return [];
        }

        var rows = await (
                from l in db.VoucherLines.AsNoTracking()
                join led in db.Ledgers.AsNoTracking() on l.LedgerId equals led.Id
                where l.CompanyId == companyId && voucherIds.Contains(l.VoucherId) && l.LineNo == 1
                select new { l.VoucherId, led.Name, l.Side })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.VoucherId, x => (x.Name, x.Side));
    }

    public static async Task<Dictionary<Guid, string>> VoucherTypeNamesAsync(IAccountingDbContext db, Guid companyId, CancellationToken cancellationToken) =>
        await db.VoucherTypes.AsNoTracking().Where(t => t.CompanyId == companyId).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
}
