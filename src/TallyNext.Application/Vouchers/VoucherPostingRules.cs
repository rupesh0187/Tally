using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Vouchers;

/// <summary>
/// Cross-aggregate voucher validation shared by direct saves (web, API, desktop) and by the server when it
/// accepts vouchers uploaded by devices, so that every path enforces identical rules.
/// </summary>
public sealed class VoucherPostingRules(IAccountingDbContext db)
{
    /// <summary>
    /// Verifies that every referenced ledger exists in the voucher's company and is not deleted; when
    /// <paramref name="forPosting"/> is true also applies the balance check and the per-type ledger rules.
    /// </summary>
    public async Task ValidateAsync(Voucher voucher, bool forPosting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        var ledgerIds = voucher.Lines.Select(l => l.LedgerId).Distinct().ToList();
        var ledgers = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == voucher.CompanyId && ledgerIds.Contains(l.Id))
            .Select(l => new { l.Id, l.GroupId, l.IsDeleted })
            .ToListAsync(cancellationToken);

        foreach (var line in voucher.Lines)
        {
            var ledger = ledgers.FirstOrDefault(l => l.Id == line.LedgerId)
                ?? throw new DomainException("voucher.ledger_not_found", $"Line {line.LineNo}: the ledger was not found.");
            if (ledger.IsDeleted)
            {
                throw new DomainException("voucher.ledger_deleted", $"Line {line.LineNo}: the ledger has been deleted.");
            }
        }

        if (!forPosting)
        {
            return;
        }

        voucher.EnsurePostable();
        var groups = await db.AccountGroups.AsNoTracking().Where(g => g.CompanyId == voucher.CompanyId).ToListAsync(cancellationToken);
        var hierarchy = new GroupHierarchy(groups);
        var traits = ledgers.ToDictionary(l => l.Id, l => hierarchy.TraitsOf(l.GroupId));
        VoucherTypeRules.Validate(voucher, traits);
    }
}

/// <summary>
/// Allocates voucher numbers from this store's series. Must be called inside the company write scope, which
/// serializes allocation; the unique index on (company, type, year, series, number) is the final safeguard.
/// </summary>
public sealed class VoucherNumberAllocator(IAccountingDbContext db, IVoucherSeriesProvider seriesProvider)
{
    public async Task<VoucherNumber> AllocateAsync(ICompanyWriteScope scope, Voucher voucher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(voucher);
        if (scope.CompanyId != voucher.CompanyId)
        {
            throw new InvalidOperationException("Voucher numbers must be allocated inside the voucher company's write scope.");
        }

        var seriesCode = await seriesProvider.GetSeriesCodeAsync(voucher.CompanyId, cancellationToken);
        var series = db.VoucherNumberSeries.Local.FirstOrDefault(s => Matches(s, voucher, seriesCode))
            ?? await db.VoucherNumberSeries.SingleOrDefaultAsync(
                s => s.CompanyId == voucher.CompanyId
                     && s.VoucherTypeId == voucher.VoucherTypeId
                     && s.FinancialYearStart == voucher.FinancialYearStart
                     && s.SeriesCode == seriesCode,
                cancellationToken);

        if (series is null)
        {
            series = VoucherNumberSeries.Create(voucher.CompanyId, voucher.VoucherTypeId, new FinancialYear(voucher.FinancialYearStart), seriesCode);

            // Vouchers from this series may already exist (e.g. restored or re-downloaded data); never reuse their numbers.
            var highest = await db.Vouchers.AsNoTracking()
                .Where(v => v.CompanyId == voucher.CompanyId
                            && v.VoucherTypeId == voucher.VoucherTypeId
                            && v.FinancialYearStart == voucher.FinancialYearStart
                            && v.SeriesCode == seriesCode)
                .MaxAsync(v => v.SequenceNumber, cancellationToken);
            if (highest is { } h)
            {
                series.EnsureAtLeast(h);
            }

            db.VoucherNumberSeries.Add(series);
        }

        return series.AllocateNext();
    }

    private static bool Matches(VoucherNumberSeries s, Voucher v, string seriesCode) =>
        s.CompanyId == v.CompanyId && s.VoucherTypeId == v.VoucherTypeId && s.FinancialYearStart == v.FinancialYearStart && s.SeriesCode == seriesCode;
}
