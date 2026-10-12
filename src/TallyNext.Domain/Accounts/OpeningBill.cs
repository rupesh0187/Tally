using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Domain.Accounts;

/// <summary>
/// A bill outstanding at the books-beginning date on a bill-wise ledger (the bill-wise breakup of its opening
/// balance). Any part of the opening balance not covered by opening bills is treated as on account.
/// </summary>
public sealed record OpeningBill(string Name, DateOnly Date, DateOnly? DueDate, long AmountMinor, DrCr Side)
{
    public const int MaxPerLedger = 2_000;

    public long SignedAmountMinor => Side == DrCr.Debit ? AmountMinor : -AmountMinor;

    public static IReadOnlyList<OpeningBill> Normalize(IEnumerable<OpeningBill>? bills, bool isBillWise)
    {
        var list = new List<OpeningBill>();
        foreach (var bill in bills ?? [])
        {
            var name = BillNames.Clean(bill.Name)
                ?? throw new DomainException("ledger.opening_bill_name_required", "Every opening bill needs a reference.");
            if (bill.AmountMinor <= 0)
            {
                throw new DomainException("ledger.opening_bill_amount_invalid", $"Opening bill '{name}': the amount must be greater than zero.");
            }

            if (!bill.Side.IsDefined())
            {
                throw new DomainException("ledger.opening_bill_side_invalid", $"Opening bill '{name}': choose Dr or Cr.");
            }

            if (bill.DueDate is { } due && due < bill.Date)
            {
                throw new DomainException("ledger.opening_bill_due_invalid", $"Opening bill '{name}': the due date is before the bill date.");
            }

            list.Add(bill with { Name = name });
        }

        if (list.Count > 0 && !isBillWise)
        {
            throw new DomainException("ledger.opening_bills_not_billwise", "Opening bills can only be entered for a ledger that maintains bill-wise details.");
        }

        if (list.Count > MaxPerLedger)
        {
            throw new DomainException("ledger.too_many_opening_bills", $"A ledger can have at most {MaxPerLedger} opening bills.");
        }

        if (list.GroupBy(b => BillNames.Normalize(b.Name)).Any(g => g.Count() > 1))
        {
            throw new DomainException("ledger.opening_bill_duplicate", "Two opening bills have the same reference.");
        }

        return list.OrderBy(b => b.Date).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
