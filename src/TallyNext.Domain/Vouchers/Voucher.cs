using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Vouchers;

public enum VoucherStatus : byte
{
    /// <summary>Work in progress: editable, may be unbalanced, no number, no effect on balances.</summary>
    Draft = 1,

    /// <summary>Balanced, numbered and immutable. Affects balances. Corrected only by reversal.</summary>
    Posted = 2,

    /// <summary>An abandoned draft. Kept for the audit trail; never had an effect on balances.</summary>
    Cancelled = 3,
}

public sealed record VoucherHeader(string? Narration = null, string? Reference = null, DateOnly? ReferenceDate = null);

/// <param name="Id">Optional line id; replicated vouchers keep the ids assigned where they were created.</param>
public readonly record struct VoucherLineSpec(Guid LedgerId, DrCr Side, Money Amount, string? Narration = null, Guid? Id = null);

/// <summary>
/// A double-entry voucher (aggregate root). Invariants enforced here:
/// <list type="bullet">
/// <item>Every line has a positive amount and a debit or credit side.</item>
/// <item>A voucher can only be posted when it has at least one debit and one credit line and total debits equal total credits.</item>
/// <item>Posted vouchers are immutable: no edits, no deletion, no cancellation. Corrections are made with a reversal voucher.</item>
/// <item>A posted voucher always carries a number from exactly one series.</item>
/// </list>
/// Rules that need other aggregates (ledger existence, cash/bank classification, period locks) are applied by the
/// application layer through <see cref="VoucherTypeRules"/> and <see cref="Company.EnsureDateIsOpen"/>.
/// </summary>
public sealed class Voucher : CompanyScopedEntity
{
    public const int MaxLines = 500;

    private readonly List<VoucherLine> _lines = [];
    private readonly List<VoucherItemLine> _itemLines = [];

    private Voucher()
    {
    }

    public Guid VoucherTypeId { get; private set; }

    public VoucherBaseType BaseType { get; private set; }

    public DateOnly Date { get; private set; }

    public DateOnly FinancialYearStart { get; private set; }

    public VoucherStatus Status { get; private set; }

    public string? SeriesCode { get; private set; }

    public long? SequenceNumber { get; private set; }

    /// <summary>Display form of the number, e.g. "17" (server series) or "D3K/17" (device series).</summary>
    public string? Number { get; private set; }

    public string? Narration { get; private set; }

    public string? Reference { get; private set; }

    public DateOnly? ReferenceDate { get; private set; }

    /// <summary>Set on a reversal voucher; points at the posted voucher it cancels out. Unique.</summary>
    public Guid? ReversalOfVoucherId { get; private set; }

    /// <summary>Sum of the debit lines (= sum of credit lines once posted), in minor units.</summary>
    public long TotalMinor { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid? CreatedOnDeviceId { get; private set; }

    public Guid? PostedByUserId { get; private set; }

    public DateTime? PostedAtUtc { get; private set; }

    /// <summary>Client-supplied key making API creates safely retryable. Unique per company when present.</summary>
    public string? IdempotencyKey { get; private set; }

    public IReadOnlyList<VoucherLine> Lines => _lines;

    /// <summary>Invoice item/service lines (empty for plain accounting vouchers).</summary>
    public IReadOnlyList<VoucherItemLine> ItemLines => _itemLines;

    /// <summary>Customer or supplier of an invoice; null for plain accounting vouchers.</summary>
    public Guid? PartyLedgerId { get; private set; }

    /// <summary>Place of supply (GST State code) used to determine <see cref="SupplyNature"/>.</summary>
    public string? PlaceOfSupplyStateCode { get; private set; }

    public SupplyNature? SupplyNature { get; private set; }

    public bool IsInvoice => PartyLedgerId is not null;

    public bool IsReversal => ReversalOfVoucherId is not null;

    public Money Total => Money.FromMinor(TotalMinor);

    public Money TotalDebit => Money.FromMinor(SumSide(DrCr.Debit));

    public Money TotalCredit => Money.FromMinor(SumSide(DrCr.Credit));

    public bool IsBalanced => SumSide(DrCr.Debit) == SumSide(DrCr.Credit);

    public static Voucher CreateDraft(
        Guid id,
        Company company,
        VoucherType type,
        DateOnly date,
        VoucherHeader header,
        IReadOnlyCollection<VoucherLineSpec> lines,
        Guid userId,
        Guid? deviceId,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(type);
        if (id == Guid.Empty)
        {
            throw new DomainException("voucher.id_required", "A voucher identifier is required.");
        }

        if (type.CompanyId != company.Id)
        {
            throw new DomainException("voucher.type_invalid", "The voucher type does not belong to this company.");
        }

        if (!type.IsActive || type.IsDeleted)
        {
            throw new DomainException("voucher.type_inactive", "The voucher type is not active.");
        }

        var voucher = new Voucher
        {
            Id = id,
            CompanyId = company.Id,
            VoucherTypeId = type.Id,
            BaseType = type.BaseType,
            Status = VoucherStatus.Draft,
            CreatedByUserId = userId,
            CreatedOnDeviceId = deviceId,
            IdempotencyKey = NameRules.CleanOptional(idempotencyKey, 100, "idempotency_key"),
        };
        voucher.SetHeader(company, date, header);
        voucher.SetLines(lines);
        return voucher;
    }

    /// <summary>
    /// Creates (as a draft) the mirror image of a posted voucher. The caller posts it with a number.
    /// The reversal keeps the original voucher type so it appears in the same register.
    /// </summary>
    public static Voucher CreateReversal(Guid id, Company company, Voucher original, DateOnly date, string? narration, Guid userId, Guid? deviceId)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(original);
        if (original.CompanyId != company.Id)
        {
            throw new DomainException("voucher.not_found", "The voucher was not found.");
        }

        if (original.Status != VoucherStatus.Posted)
        {
            throw new DomainException("voucher.reverse_not_posted", "Only posted vouchers can be reversed.");
        }

        if (original.IsReversal)
        {
            throw new DomainException("voucher.reverse_reversal", "A reversal voucher cannot itself be reversed; enter a new voucher instead.");
        }

        if (date < original.Date)
        {
            throw new DomainException("voucher.reverse_date", "A reversal cannot be dated before the voucher it reverses.");
        }

        var reversal = new Voucher
        {
            Id = id,
            CompanyId = company.Id,
            VoucherTypeId = original.VoucherTypeId,
            BaseType = original.BaseType,
            Status = VoucherStatus.Draft,
            ReversalOfVoucherId = original.Id,
            CreatedByUserId = userId,
            CreatedOnDeviceId = deviceId,
        };
        reversal.SetHeader(
            company,
            date,
            new VoucherHeader(narration ?? $"Reversal of {original.Number}", original.Reference, original.ReferenceDate));
        reversal.SetLines(original.Lines
            .OrderBy(l => l.LineNo)
            .Select(l => new VoucherLineSpec(l.LedgerId, l.Side.Opposite(), l.Amount, l.Narration))
            .ToList());
        if (original.IsInvoice)
        {
            // Same goods and tax with the stock movement turned around, so stock and GST registers net to zero.
            reversal.PartyLedgerId = original.PartyLedgerId;
            reversal.PlaceOfSupplyStateCode = original.PlaceOfSupplyStateCode;
            reversal.SupplyNature = original.SupplyNature;
            reversal.ReplaceItemLines(original.ItemLines.OrderBy(l => l.LineNo).Select(l => l.ToReversedSpec()).ToList());
        }

        return reversal;
    }

    public void UpdateDraft(Company company, DateOnly date, VoucherHeader header, IReadOnlyCollection<VoucherLineSpec> lines)
    {
        ArgumentNullException.ThrowIfNull(company);
        EnsureDraft();
        if (IsReversal)
        {
            throw new DomainException("voucher.reversal_readonly", "Reversal lines are derived from the original voucher and cannot be edited.");
        }

        SetHeader(company, date, header);
        SetLines(lines);
    }

    /// <summary>
    /// Attaches invoice details to a draft: the party, the place of supply and the item/service lines from which the
    /// accounting lines were built (see <see cref="InvoiceBuilder"/>). Stock lines must move in the direction of the
    /// voucher type (out for Sales and Debit Note, in for Purchase and Credit Note).
    /// </summary>
    public void SetInvoiceDetails(Guid partyLedgerId, string? placeOfSupplyStateCode, SupplyNature nature, IReadOnlyCollection<VoucherItemLineSpec> itemLines)
    {
        ArgumentNullException.ThrowIfNull(itemLines);
        EnsureDraft();
        if (!InvoiceBuilder.IsInvoiceType(BaseType))
        {
            throw new DomainException("invoice.type_invalid", "Only Sales, Purchase, Credit Note and Debit Note vouchers carry invoice lines.");
        }

        if (!Enum.IsDefined(nature))
        {
            throw new DomainException("invoice.nature_invalid", "The supply type is not valid.");
        }

        if (placeOfSupplyStateCode is not null && !IndianStates.IsValidCode(placeOfSupplyStateCode))
        {
            throw new DomainException("invoice.place_of_supply_invalid", $"'{placeOfSupplyStateCode}' is not a valid GST State code.");
        }

        var expected = InvoiceBuilder.StockDirectionOf(BaseType);
        if (itemLines.Any(l => l.Kind == InvoiceLineKind.Stock && l.Direction != expected))
        {
            throw new DomainException("invoice.stock_direction_invalid", "The stock movement does not match the voucher type.");
        }

        PartyLedgerId = partyLedgerId;
        PlaceOfSupplyStateCode = placeOfSupplyStateCode;
        SupplyNature = nature;
        ReplaceItemLines(itemLines);
    }

    /// <summary>
    /// Posts the voucher. The date must be open in <paramref name="company"/>; balance and line structure are
    /// re-validated here. Type-specific ledger rules must already have been checked with <see cref="VoucherTypeRules"/>.
    /// </summary>
    public void Post(Company company, VoucherNumber number, Guid userId, DateTime postedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(company);
        EnsureDraft();
        company.EnsureDateIsOpen(Date);
        EnsurePostable();
        if (!VoucherNumber.IsValidSeriesCode(number.SeriesCode) || number.Sequence <= 0)
        {
            throw new DomainException("voucher.number_invalid", "The voucher number is not valid.");
        }

        SeriesCode = number.SeriesCode;
        SequenceNumber = number.Sequence;
        Number = number.Display;
        Status = VoucherStatus.Posted;
        PostedByUserId = userId;
        PostedAtUtc = postedAtUtc;
        foreach (var line in _lines)
        {
            line.MarkPosted();
        }

        foreach (var item in _itemLines)
        {
            item.MarkPosted();
        }
    }

    public void CancelDraft()
    {
        if (Status == VoucherStatus.Posted)
        {
            throw new DomainException("voucher.posted_immutable", "A posted voucher cannot be cancelled; reverse it instead.");
        }

        EnsureDraft();
        Status = VoucherStatus.Cancelled;
    }

    /// <summary>Validates the structural posting invariants without changing state.</summary>
    public void EnsurePostable()
    {
        if (_lines.Count < 2)
        {
            throw new DomainException("voucher.too_few_lines", "A voucher needs at least two lines.");
        }

        if (!_lines.Any(l => l.Side == DrCr.Debit) || !_lines.Any(l => l.Side == DrCr.Credit))
        {
            throw new DomainException("voucher.one_sided", "A voucher needs at least one debit and one credit line.");
        }

        var debit = SumSide(DrCr.Debit);
        var credit = SumSide(DrCr.Credit);
        if (debit != credit)
        {
            throw new DomainException(
                "voucher.unbalanced",
                $"Total debits ({Money.FromMinor(debit)}) must equal total credits ({Money.FromMinor(credit)}).");
        }
    }

    private void SetHeader(Company company, DateOnly date, VoucherHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (date < company.BooksBeginningDate)
        {
            throw new DomainException(
                "voucher.before_books_beginning",
                $"The date {date:yyyy-MM-dd} is before the books beginning date {company.BooksBeginningDate:yyyy-MM-dd}.");
        }

        Date = date;
        FinancialYearStart = company.FinancialYearOf(date).Start;
        Narration = NameRules.CleanOptional(header.Narration, 1000, "narration");
        Reference = NameRules.CleanOptional(header.Reference, 100, "reference");
        ReferenceDate = header.ReferenceDate;
        foreach (var line in _lines)
        {
            line.SetVoucherDate(date);
        }

        foreach (var item in _itemLines)
        {
            item.SetVoucherDate(date);
        }
    }

    private void SetLines(IReadOnlyCollection<VoucherLineSpec> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new DomainException("voucher.no_lines", "A voucher needs at least one line.");
        }

        if (lines.Count > MaxLines)
        {
            throw new DomainException("voucher.too_many_lines", $"A voucher can have at most {MaxLines} lines.");
        }

        var newLines = new List<VoucherLine>(lines.Count);
        var lineNo = 0;
        long debit = 0;
        foreach (var spec in lines)
        {
            lineNo++;
            if (spec.LedgerId == Guid.Empty)
            {
                throw new DomainException("voucher.line_ledger_required", $"Line {lineNo}: a ledger is required.");
            }

            if (!spec.Side.IsDefined())
            {
                throw new DomainException("voucher.line_side_invalid", $"Line {lineNo}: choose Dr or Cr.");
            }

            if (!spec.Amount.IsPositive)
            {
                throw new DomainException("voucher.line_amount_invalid", $"Line {lineNo}: the amount must be greater than zero.");
            }

            if (spec.Side == DrCr.Debit)
            {
                debit = checked(debit + spec.Amount.Minor);
            }

            newLines.Add(VoucherLine.Create(this, lineNo, spec));
        }

        _lines.Clear();
        _lines.AddRange(newLines);
        TotalMinor = debit;
    }

    private void ReplaceItemLines(IReadOnlyCollection<VoucherItemLineSpec> itemLines)
    {
        if (itemLines.Count > InvoiceBuilder.MaxLines)
        {
            throw new DomainException("invoice.too_many_lines", $"An invoice can have at most {InvoiceBuilder.MaxLines} lines.");
        }

        var lineNo = 0;
        var created = itemLines.Select(spec => VoucherItemLine.Create(this, ++lineNo, spec)).ToList();
        _itemLines.Clear();
        _itemLines.AddRange(created);
    }

    private long SumSide(DrCr side)
    {
        long total = 0;
        foreach (var line in _lines)
        {
            if (line.Side == side)
            {
                total = checked(total + line.AmountMinor);
            }
        }

        return total;
    }

    private void EnsureDraft()
    {
        switch (Status)
        {
            case VoucherStatus.Draft:
                return;
            case VoucherStatus.Posted:
                throw new DomainException("voucher.posted_immutable", "A posted voucher cannot be changed; reverse it instead.");
            default:
                throw new DomainException("voucher.cancelled", "The voucher has been cancelled.");
        }
    }
}

/// <summary>One debit or credit posting of a voucher.</summary>
/// <remarks>
/// <see cref="VoucherDate"/> and <see cref="IsPosted"/> are denormalized copies of the parent's values so that ledger
/// reports and trial balances can be answered from a single covering index on (CompanyId, LedgerId, VoucherDate).
/// They can only change while the voucher is a draft, so the copies cannot drift.
/// </remarks>
public sealed class VoucherLine
{
    private VoucherLine()
    {
    }

    public Guid Id { get; private set; }

    public Guid VoucherId { get; private set; }

    public Guid CompanyId { get; private set; }

    public int LineNo { get; private set; }

    public Guid LedgerId { get; private set; }

    public DrCr Side { get; private set; }

    /// <summary>Always positive, in minor units.</summary>
    public long AmountMinor { get; private set; }

    public string? Narration { get; private set; }

    public DateOnly VoucherDate { get; private set; }

    public bool IsPosted { get; private set; }

    public Money Amount => Money.FromMinor(AmountMinor);

    public long SignedAmountMinor => Side == DrCr.Debit ? AmountMinor : -AmountMinor;

    internal static VoucherLine Create(Voucher voucher, int lineNo, VoucherLineSpec spec) => new()
    {
        Id = spec.Id is { } id && id != Guid.Empty ? id : Ids.New(),
        VoucherId = voucher.Id,
        CompanyId = voucher.CompanyId,
        LineNo = lineNo,
        LedgerId = spec.LedgerId,
        Side = spec.Side,
        AmountMinor = spec.Amount.Minor,
        Narration = NameRules.CleanOptional(spec.Narration, 500, "line_narration"),
        VoucherDate = voucher.Date,
        IsPosted = false,
    };

    internal void MarkPosted() => IsPosted = true;

    internal void SetVoucherDate(DateOnly date) => VoucherDate = date;
}
