using TallyNext.Domain.Common;

namespace TallyNext.Domain.Vouchers;

/// <summary>How an amount posted to a bill-wise ledger relates to the party's bills.</summary>
public enum BillAllocationKind : byte
{
    /// <summary>Creates a bill (e.g. an invoice). An empty name becomes the voucher number when posted.</summary>
    NewRef = 1,

    /// <summary>Settles (fully or partly) an existing bill.</summary>
    AgstRef = 2,

    /// <summary>An amount paid or received in advance, kept under its own reference until adjusted.</summary>
    Advance = 3,

    /// <summary>Not linked to any bill.</summary>
    OnAccount = 4,
}

/// <param name="BillName">Bill reference; required for <see cref="BillAllocationKind.AgstRef"/> and
/// <see cref="BillAllocationKind.Advance"/>, optional for <see cref="BillAllocationKind.NewRef"/> (defaults to the
/// voucher number), absent for <see cref="BillAllocationKind.OnAccount"/>.</param>
/// <param name="DueDate">Due date of a new bill or advance.</param>
public readonly record struct BillAllocationSpec(BillAllocationKind Kind, string? BillName, DateOnly? DueDate, Money Amount, Guid? Id = null);

/// <summary>Bill reference rules shared by allocations and opening bills.</summary>
public static class BillNames
{
    public const int MaxLength = 50;

    public const int MaxAllocationsPerLine = 100;

    /// <summary>Cleans a bill name (trimmed, inner whitespace collapsed); null when empty.</summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var cleaned = NameRules.Clean(name, "bill_name");
        return cleaned.Length <= MaxLength
            ? cleaned
            : throw new DomainException("bill_name.too_long", $"A bill reference has at most {MaxLength} characters.");
    }

    /// <summary>Bills are matched case-insensitively within a ledger.</summary>
    public static string Normalize(string cleanedName) => NameRules.Normalize(cleanedName);
}

/// <summary>
/// The bill-wise breakup of one voucher line posted to a bill-wise ledger (customer, supplier). A bill is identified
/// by its ledger and its (case-insensitive) name: every allocation with that name adds to or settles the same bill, so
/// references made on different devices meet without any shared counter.
/// </summary>
/// <remarks><see cref="VoucherDate"/>, <see cref="Side"/> and <see cref="IsPosted"/> are denormalized from the voucher
/// and its line (they can only change while the voucher is a draft) so outstanding reports read one index.</remarks>
public sealed class VoucherBillAllocation
{
    private VoucherBillAllocation()
    {
    }

    public Guid Id { get; private set; }

    public Guid VoucherId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary>Number of the voucher line this allocation belongs to.</summary>
    public int LineNo { get; private set; }

    public int AllocationNo { get; private set; }

    public Guid LedgerId { get; private set; }

    public DrCr Side { get; private set; }

    public BillAllocationKind Kind { get; private set; }

    public string? BillName { get; private set; }

    public string? NormalizedBillName { get; private set; }

    public DateOnly? DueDate { get; private set; }

    /// <summary>Always positive, in minor units; the side is the line's side.</summary>
    public long AmountMinor { get; private set; }

    public DateOnly VoucherDate { get; private set; }

    public bool IsPosted { get; private set; }

    public long SignedAmountMinor => Side == DrCr.Debit ? AmountMinor : -AmountMinor;

    internal static VoucherBillAllocation Create(Voucher voucher, VoucherLine line, int allocationNo, BillAllocationSpec spec)
    {
        var name = BillNames.Clean(spec.BillName);
        return new VoucherBillAllocation
        {
            Id = spec.Id is { } id && id != Guid.Empty ? id : Ids.New(),
            VoucherId = voucher.Id,
            CompanyId = voucher.CompanyId,
            LineNo = line.LineNo,
            AllocationNo = allocationNo,
            LedgerId = line.LedgerId,
            Side = line.Side,
            Kind = spec.Kind,
            BillName = name,
            NormalizedBillName = name is null ? null : BillNames.Normalize(name),
            DueDate = spec.DueDate,
            AmountMinor = spec.Amount.Minor,
            VoucherDate = voucher.Date,
            IsPosted = false,
        };
    }

    /// <summary>Validates the breakup of one line: positive amounts that add up to the line, names where required.</summary>
    internal static void Validate(int lineNo, Money lineAmount, IReadOnlyCollection<BillAllocationSpec> allocations)
    {
        if (allocations.Count > BillNames.MaxAllocationsPerLine)
        {
            throw new DomainException("bill.too_many_allocations", $"Line {lineNo}: at most {BillNames.MaxAllocationsPerLine} bill allocations.");
        }

        long total = 0;
        foreach (var a in allocations)
        {
            if (!Enum.IsDefined(a.Kind))
            {
                throw new DomainException("bill.kind_invalid", $"Line {lineNo}: the bill allocation type is not valid.");
            }

            if (!a.Amount.IsPositive)
            {
                throw new DomainException("bill.amount_invalid", $"Line {lineNo}: bill amounts must be greater than zero.");
            }

            var name = BillNames.Clean(a.BillName);
            switch (a.Kind)
            {
                case BillAllocationKind.OnAccount when name is not null || a.DueDate is not null:
                    throw new DomainException("bill.on_account_named", $"Line {lineNo}: an on-account amount has no bill name or due date.");
                case BillAllocationKind.AgstRef or BillAllocationKind.Advance when name is null:
                    throw new DomainException("bill.name_required", $"Line {lineNo}: enter the bill reference.");
                case BillAllocationKind.AgstRef when a.DueDate is not null:
                    throw new DomainException("bill.due_date_not_allowed", $"Line {lineNo}: a settlement has no due date of its own.");
            }

            total = checked(total + a.Amount.Minor);
        }

        if (total != lineAmount.Minor)
        {
            throw new DomainException(
                "bill.allocation_mismatch",
                $"Line {lineNo}: the bill allocations ({Money.FromMinor(total)}) must add up to the line amount ({lineAmount}).");
        }
    }

    /// <summary>The allocation a reversal carries: it settles (or re-opens) the same bill on the opposite side.</summary>
    internal BillAllocationSpec ToReversedSpec() => new(
        Kind == BillAllocationKind.OnAccount ? BillAllocationKind.OnAccount : BillAllocationKind.AgstRef,
        BillName,
        null,
        Money.FromMinor(AmountMinor));

    internal void NameAfterVoucher(string number)
    {
        if (Kind == BillAllocationKind.NewRef && BillName is null)
        {
            BillName = BillNames.Clean(number);
            NormalizedBillName = BillName is null ? null : BillNames.Normalize(BillName);
        }
    }

    internal void MarkPosted() => IsPosted = true;

    internal void SetVoucherDate(DateOnly date) => VoucherDate = date;
}
