using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Vouchers;

public enum InvoiceLineKind : byte
{
    /// <summary>Goods from inventory: quantity moves in a godown.</summary>
    Stock = 1,

    /// <summary>A service or non-inventory line: value only.</summary>
    Service = 2,
}

/// <param name="Taxable">Taxable value after discount.</param>
/// <param name="Rate">The GST rate applied (copied from the master as in force on the invoice date).</param>
public readonly record struct VoucherItemLineSpec(
    Guid? Id,
    InvoiceLineKind Kind,
    Guid? StockItemId,
    Guid? GodownId,
    Guid LedgerId,
    string? Description,
    long QuantityMilli,
    long RateScaled,
    int DiscountMilliPercent,
    Money Taxable,
    StockDirection Direction,
    GstRateEntry Rate,
    LineTax Tax);

/// <summary>
/// One line of an invoice (sales, purchase, credit note, debit note): the item or service, its quantity and value,
/// the GST rate applied and the tax computed. The voucher's accounting lines are derived from these lines; the
/// server re-derives them on every upload and rejects inconsistent invoices.
/// </summary>
public sealed class VoucherItemLine
{
    private VoucherItemLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid VoucherId { get; private set; }

    public Guid CompanyId { get; private set; }

    public int LineNo { get; private set; }

    public InvoiceLineKind Kind { get; private set; }

    public Guid? StockItemId { get; private set; }

    public Guid? GodownId { get; private set; }

    /// <summary>Sales/purchase (or income/expense) ledger the line's value is posted to.</summary>
    public Guid LedgerId { get; private set; }

    public string? Description { get; private set; }

    public long QuantityMilli { get; private set; }

    public long RateScaled { get; private set; }

    public int DiscountMilliPercent { get; private set; }

    public long TaxableMinor { get; private set; }

    public StockDirection Direction { get; private set; }

    public Taxability Taxability { get; private set; }

    public int IgstMilliPercent { get; private set; }

    public int CessMilliPercent { get; private set; }

    public string? HsnSac { get; private set; }

    public long CgstMinor { get; private set; }

    /// <summary>SGST or UTGST, depending on the voucher's supply nature.</summary>
    public long StateTaxMinor { get; private set; }

    public long IgstMinor { get; private set; }

    public long CessMinor { get; private set; }

    public DateOnly VoucherDate { get; private set; }

    public bool IsPosted { get; private set; }

    public long TaxMinor => CgstMinor + StateTaxMinor + IgstMinor + CessMinor;

    public GstRateEntry RateEntry => new(VoucherDate, Taxability, IgstMilliPercent, CessMilliPercent, HsnSac);

    internal static VoucherItemLine Create(Voucher voucher, int lineNo, VoucherItemLineSpec spec) => new()
    {
        Id = spec.Id is { } id && id != Guid.Empty ? id : Ids.New(),
        VoucherId = voucher.Id,
        CompanyId = voucher.CompanyId,
        LineNo = lineNo,
        Kind = spec.Kind,
        StockItemId = spec.StockItemId,
        GodownId = spec.GodownId,
        LedgerId = spec.LedgerId,
        Description = NameRules.CleanOptional(spec.Description, 500, "line_description"),
        QuantityMilli = spec.QuantityMilli,
        RateScaled = spec.RateScaled,
        DiscountMilliPercent = spec.DiscountMilliPercent,
        TaxableMinor = spec.Taxable.Minor,
        Direction = spec.Direction,
        Taxability = spec.Rate.Taxability,
        IgstMilliPercent = spec.Rate.IgstMilliPercent,
        CessMilliPercent = spec.Rate.CessMilliPercent,
        HsnSac = spec.Rate.HsnSac,
        CgstMinor = spec.Tax.Cgst.Minor,
        StateTaxMinor = spec.Tax.StateTax.Minor,
        IgstMinor = spec.Tax.Igst.Minor,
        CessMinor = spec.Tax.Cess.Minor,
        VoucherDate = voucher.Date,
        IsPosted = false,
    };

    /// <summary>The mirror image used by a reversal: same values, opposite stock direction.</summary>
    internal VoucherItemLineSpec ToReversedSpec() => new(
        null, Kind, StockItemId, GodownId, LedgerId, Description, QuantityMilli, RateScaled, DiscountMilliPercent, Money.FromMinor(TaxableMinor),
        Direction switch { StockDirection.In => StockDirection.Out, StockDirection.Out => StockDirection.In, _ => StockDirection.None },
        RateEntry,
        new LineTax(Money.FromMinor(CgstMinor), Money.FromMinor(StateTaxMinor), Money.FromMinor(IgstMinor), Money.FromMinor(CessMinor)));

    internal void MarkPosted() => IsPosted = true;

    internal void SetVoucherDate(DateOnly date) => VoucherDate = date;
}
