using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Vouchers;

/// <summary>
/// Cross-aggregate voucher validation shared by direct saves (web, API, desktop) and by the server when it
/// accepts vouchers uploaded by devices, so that every path enforces identical rules.
/// </summary>
public sealed partial class VoucherPostingRules(IAccountingDbContext db)
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

        await ValidateInvoiceAsync(voucher, cancellationToken);
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

public sealed partial class VoucherPostingRules
{
    /// <summary>
    /// Invoice integrity: masters exist, stock moves the right way, each line's value and tax follow from its quantity,
    /// rate and GST rate, and the accounting lines are exactly what <see cref="InvoiceBuilder"/> derives from the item
    /// lines. Anything else (a tampered or buggy client) is rejected.
    /// </summary>
    private async Task ValidateInvoiceAsync(Voucher voucher, CancellationToken cancellationToken)
    {
        if (!voucher.IsInvoice)
        {
            if (voucher.ItemLines.Count > 0)
            {
                throw new DomainException("invoice.party_required", "An invoice needs a party.");
            }

            return;
        }

        if (!InvoiceBuilder.IsInvoiceType(voucher.BaseType) || voucher.SupplyNature is not { } nature || voucher.ItemLines.Count == 0)
        {
            throw new DomainException("invoice.incomplete", "The invoice is incomplete.");
        }

        var companyId = voucher.CompanyId;
        var party = await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == voucher.PartyLedgerId, cancellationToken)
            ?? throw new DomainException("voucher.ledger_not_found", "The party ledger was not found.");
        if (party.IsDeleted)
        {
            throw new DomainException("voucher.ledger_deleted", "The party ledger has been deleted.");
        }

        var itemIds = voucher.ItemLines.Where(l => l.StockItemId is not null).Select(l => l.StockItemId!.Value).Distinct().ToList();
        var godownIds = voucher.ItemLines.Where(l => l.GodownId is not null).Select(l => l.GodownId!.Value).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var unitIds = items.Values.Select(i => i.UnitId).Distinct().ToList();
        var units = await db.Units.AsNoTracking().Where(u => u.CompanyId == companyId && unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var godowns = await db.Godowns.AsNoTracking().Where(g => g.CompanyId == companyId && godownIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, cancellationToken);

        var expectedDirection = InvoiceBuilder.StockDirectionOf(voucher.BaseType);
        if (voucher.IsReversal)
        {
            expectedDirection = expectedDirection == StockDirection.In ? StockDirection.Out : StockDirection.In;
        }

        var chargeTax = voucher.ItemLines.Any(l => l.TaxMinor != 0);
        var inputs = new List<InvoiceLineInput>(voucher.ItemLines.Count);
        foreach (var line in voucher.ItemLines.OrderBy(l => l.LineNo))
        {
            if (!Enum.IsDefined(line.Kind))
            {
                throw new DomainException("invoice.line_kind_invalid", $"Line {line.LineNo}: the line kind is not valid.");
            }

            line.RateEntry.Validate();
            if (line.Kind == InvoiceLineKind.Stock)
            {
                if (line.StockItemId is not { } itemId || !items.TryGetValue(itemId, out var item) || item.IsDeleted)
                {
                    throw new DomainException("invoice.item_not_found", $"Line {line.LineNo}: the stock item was not found.");
                }

                if (line.GodownId is not { } godownId || !godowns.TryGetValue(godownId, out var godown) || godown.IsDeleted)
                {
                    throw new DomainException("invoice.godown_not_found", $"Line {line.LineNo}: the godown was not found.");
                }

                if (line.Direction != expectedDirection)
                {
                    throw new DomainException("invoice.stock_direction_invalid", $"Line {line.LineNo}: the stock movement does not match the voucher type.");
                }

                var decimals = units.TryGetValue(item.UnitId, out var unit) ? unit.DecimalPlaces : 0;
                if (line.QuantityMilli <= 0 || line.QuantityMilli % (long)Math.Pow(10, 3 - decimals) != 0)
                {
                    throw new DomainException("invoice.line_quantity_invalid", $"Line {line.LineNo}: the quantity is not valid for its unit.");
                }

                if (StockMath.LineValue(line.QuantityMilli, line.RateScaled, line.DiscountMilliPercent).Minor != line.TaxableMinor)
                {
                    throw new DomainException("invoice.value_mismatch", $"Line {line.LineNo}: the value does not equal quantity x rate less discount.");
                }
            }
            else if (line.Direction != StockDirection.None || line.StockItemId is not null)
            {
                throw new DomainException("invoice.line_kind_invalid", $"Line {line.LineNo}: a service line cannot move stock.");
            }

            var expectedTax = chargeTax ? GstCalculator.Compute(Money.FromMinor(line.TaxableMinor), line.RateEntry, nature) : default;
            if (expectedTax.Cgst.Minor != line.CgstMinor || expectedTax.StateTax.Minor != line.StateTaxMinor
                || expectedTax.Igst.Minor != line.IgstMinor || expectedTax.Cess.Minor != line.CessMinor)
            {
                throw new DomainException("invoice.tax_mismatch", $"Line {line.LineNo}: the tax does not match the rate and supply type.");
            }

            inputs.Add(new InvoiceLineInput(
                line.Id, line.Kind, line.StockItemId, line.GodownId, line.LedgerId, line.Description, line.QuantityMilli, line.RateScaled,
                line.DiscountMilliPercent, line.Kind == InvoiceLineKind.Service ? Money.FromMinor(line.TaxableMinor) : null, line.RateEntry));
        }

        // Re-derive the postings from the item lines, using the voucher's own tax and round-off ledgers.
        var lineLedgerIds = voucher.Lines.Select(l => l.LedgerId).Distinct().ToList();
        var ledgerInfo = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && lineLedgerIds.Contains(l.Id))
            .Select(l => new { l.Id, l.GstTaxType })
            .ToListAsync(cancellationToken);
        Guid? Tax(GstTaxType type) => ledgerInfo.Where(l => l.GstTaxType == type).Select(l => (Guid?)l.Id).FirstOrDefault();
        var taxLedgers = new InvoiceTaxLedgers(Tax(GstTaxType.Cgst), Tax(GstTaxType.Sgst), Tax(GstTaxType.Utgst), Tax(GstTaxType.Igst), Tax(GstTaxType.Cess));
        var itemLedgerIds = voucher.ItemLines.Select(l => l.LedgerId).ToHashSet();
        var roundOffCandidates = ledgerInfo
            .Where(l => l.GstTaxType == GstTaxType.None && l.Id != voucher.PartyLedgerId && !itemLedgerIds.Contains(l.Id))
            .Select(l => l.Id)
            .ToList();
        if (roundOffCandidates.Count > 1)
        {
            throw new DomainException("invoice.accounting_mismatch", "The invoice posts to ledgers that its lines do not explain.");
        }

        var rebuilt = InvoiceBuilder.Build(voucher.BaseType, party.Id, nature, chargeTax, inputs, taxLedgers, roundOffCandidates.Count == 1 ? roundOffCandidates[0] : null);
        var expected = rebuilt.AccountingLines
            .Select(l => (l.LedgerId, Side: voucher.IsReversal ? l.Side.Opposite() : l.Side, l.Amount.Minor))
            .OrderBy(l => l.LedgerId).ThenBy(l => l.Side).ThenBy(l => l.Minor)
            .ToList();
        var actual = voucher.Lines
            .Select(l => (l.LedgerId, l.Side, Minor: l.AmountMinor))
            .OrderBy(l => l.LedgerId).ThenBy(l => l.Side).ThenBy(l => l.Minor)
            .ToList();
        if (!expected.SequenceEqual(actual))
        {
            throw new DomainException("invoice.accounting_mismatch", "The invoice accounting entries do not match its lines and taxes.");
        }
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
