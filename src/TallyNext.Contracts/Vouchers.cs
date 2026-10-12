using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Contracts.Vouchers;

public sealed record VoucherTypeDto(Guid Id, string Name, VoucherBaseType BaseType, string Abbreviation, bool IsSystem, bool IsActive);

public sealed class VoucherLineInput
{
    public Guid LedgerId { get; set; }

    public DrCr Side { get; set; } = DrCr.Debit;

    /// <summary>Positive amount with at most two decimal places. Never rounded silently.</summary>
    public decimal Amount { get; set; }

    public string? Narration { get; set; }

    /// <summary>
    /// Bill-wise breakup for a bill-wise ledger. When omitted, a default is applied: on account for Contra, Payment and
    /// Receipt; otherwise a new bill named after the voucher number, due after the ledger's credit period.
    /// </summary>
    public List<BillAllocationInput>? Bills { get; set; }
}

/// <summary>One bill allocation of a voucher line.</summary>
public sealed class BillAllocationInput
{
    public BillAllocationKind Kind { get; set; } = BillAllocationKind.NewRef;

    /// <summary>Bill reference. For a new bill, empty means "the voucher number".</summary>
    public string? BillName { get; set; }

    /// <summary>For a new bill or advance; defaults to the voucher date plus the ledger's credit period.</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Amount allocated; at most one allocation per line may leave it empty to take the remainder.</summary>
    public decimal? Amount { get; set; }
}

public sealed record BillAllocationDto(Guid Id, BillAllocationKind Kind, string? BillName, DateOnly? DueDate, decimal Amount);

public sealed class VoucherInput
{
    /// <summary>Optional client-generated UUIDv7. Re-sending the same id with the same content is idempotent.</summary>
    public Guid? Id { get; set; }

    public Guid VoucherTypeId { get; set; }

    public DateOnly Date { get; set; }

    public string? Narration { get; set; }

    public string? Reference { get; set; }

    public DateOnly? ReferenceDate { get; set; }

    public List<VoucherLineInput> Lines { get; set; } = [];

    /// <summary>True posts immediately (must balance); false saves a draft.</summary>
    public bool Post { get; set; } = true;

    /// <summary>Client retry key (also accepted as the Idempotency-Key HTTP header).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>For draft updates: fails with a conflict if the voucher changed since this version.</summary>
    public long? ExpectedVersion { get; set; }
}

public sealed record VoucherLineDto(Guid Id, int LineNo, Guid LedgerId, string LedgerName, DrCr Side, decimal Amount, string? Narration, IReadOnlyList<BillAllocationDto>? Bills = null);

public sealed record VoucherDto(
    Guid Id,
    Guid VoucherTypeId,
    string VoucherTypeName,
    VoucherBaseType BaseType,
    DateOnly Date,
    string FinancialYear,
    string? Number,
    VoucherStatus Status,
    string? Narration,
    string? Reference,
    DateOnly? ReferenceDate,
    Guid? ReversalOfVoucherId,
    string? ReversalOfNumber,
    Guid? ReversedByVoucherId,
    string? ReversedByNumber,
    decimal Total,
    IReadOnlyList<VoucherLineDto> Lines,
    Guid CreatedByUserId,
    Guid? CreatedOnDeviceId,
    DateTime CreatedAtUtc,
    DateTime? PostedAtUtc,
    long Version,
    InvoiceDetailsDto? Invoice = null);

/// <summary>Invoice part of a voucher (null for plain accounting vouchers).</summary>
/// <param name="GrandTotal">Amount charged to the party (after round-off). The voucher's Total is the debit total, which
/// also includes a debit round-off line.</param>
public sealed record InvoiceDetailsDto(
    Guid PartyLedgerId,
    string PartyName,
    string? PlaceOfSupplyStateCode,
    SupplyNature SupplyNature,
    IReadOnlyList<InvoiceLineDto> Lines,
    decimal Taxable,
    decimal Cgst,
    decimal StateTax,
    decimal Igst,
    decimal Cess,
    decimal RoundOff = 0,
    decimal GrandTotal = 0);

public sealed record InvoiceLineDto(
    Guid Id,
    int LineNo,
    InvoiceLineKind Kind,
    Guid? StockItemId,
    string? ItemName,
    Guid? GodownId,
    string? GodownName,
    Guid LedgerId,
    string LedgerName,
    string? Description,
    decimal Quantity,
    string? UnitSymbol,
    decimal Rate,
    decimal DiscountPercent,
    decimal Taxable,
    StockDirection Direction,
    Taxability Taxability,
    decimal GstPercent,
    string? HsnSac,
    decimal Cgst,
    decimal StateTax,
    decimal Igst,
    decimal Cess);

/// <summary>Sales / purchase / credit note / debit note with item or service lines; taxes are computed.</summary>
public sealed class InvoiceRequest
{
    public Guid? Id { get; set; }

    public Guid VoucherTypeId { get; set; }

    public DateOnly Date { get; set; }

    public Guid PartyLedgerId { get; set; }

    /// <summary>Overrides the default place of supply (party's State for sales, company's State for purchases).</summary>
    public string? PlaceOfSupplyStateCode { get; set; }

    public string? Narration { get; set; }

    public string? Reference { get; set; }

    public DateOnly? ReferenceDate { get; set; }

    /// <summary>Round the invoice total to the nearest rupee through the Round Off ledger.</summary>
    public bool RoundOff { get; set; } = true;

    public bool Post { get; set; } = true;

    public string? IdempotencyKey { get; set; }

    public long? ExpectedVersion { get; set; }

    /// <summary>
    /// Bill reference of the party amount when the party ledger is bill-wise (the amount is always the invoice total).
    /// Defaults to a new bill named after the voucher number, due after the party's credit period.
    /// </summary>
    public BillAllocationInput? PartyBill { get; set; }

    public List<InvoiceRequestLine> Lines { get; set; } = [];
}

public sealed class InvoiceRequestLine
{
    public InvoiceLineKind Kind { get; set; } = InvoiceLineKind.Stock;

    public Guid? StockItemId { get; set; }

    public Guid? GodownId { get; set; }

    /// <summary>Sales/purchase ledger; defaults to the first ledger under Sales or Purchase Accounts.</summary>
    public Guid? LedgerId { get; set; }

    public string? Description { get; set; }

    public decimal Quantity { get; set; }

    public decimal Rate { get; set; }

    public decimal DiscountPercent { get; set; }

    /// <summary>Value of a service line (instead of quantity x rate).</summary>
    public decimal? Amount { get; set; }
}

/// <summary>Result of computing an invoice without saving it (live totals in the entry screen).</summary>
public sealed record InvoicePreview(
    SupplyNature SupplyNature,
    string? PlaceOfSupplyStateCode,
    bool ChargesGst,
    IReadOnlyList<InvoicePreviewLine> Lines,
    decimal Taxable,
    decimal Cgst,
    decimal StateTax,
    decimal Igst,
    decimal Cess,
    decimal RoundOff,
    decimal GrandTotal);

public sealed record InvoicePreviewLine(decimal Taxable, decimal GstPercent, string? HsnSac, decimal Tax);

public sealed record VoucherListItem(
    Guid Id,
    DateOnly Date,
    string? Number,
    string VoucherTypeName,
    VoucherBaseType BaseType,
    VoucherStatus Status,
    string Particulars,
    decimal Total,
    string? Narration,
    bool IsReversal,
    long Version);

public sealed class VoucherQuery
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public Guid? VoucherTypeId { get; set; }

    public VoucherStatus? Status { get; set; }

    /// <summary>Matches voucher number, reference or narration.</summary>
    public string? Search { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = Paging.DefaultPageSize;
}

public sealed class ReverseVoucherRequest
{
    public DateOnly Date { get; set; }

    public string? Narration { get; set; }

    /// <summary>Optional client-generated id of the reversal voucher (idempotent retries).</summary>
    public Guid? ReversalId { get; set; }
}
