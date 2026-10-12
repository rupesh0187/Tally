using TallyNext.Domain.Common;

namespace TallyNext.Domain.Vouchers;

/// <summary>Classification of a ledger derived from its group ancestry (see <see cref="Accounts.SystemGroupKeys"/>).</summary>
[Flags]
public enum LedgerTraits
{
    None = 0,
    CashOrBank = 1,
    SalesAccount = 2,
    PurchaseAccount = 4,
}

/// <summary>
/// Ledger rules per voucher type, applied when posting (not to reversals, which mirror an already-valid voucher).
/// <list type="table">
/// <item><term>Contra</term><description>Every line is a cash or bank ledger (deposits, withdrawals, transfers).</description></item>
/// <item><term>Payment</term><description>At least one credit line to cash/bank. Other credits (e.g. TDS payable) are allowed.</description></item>
/// <item><term>Receipt</term><description>At least one debit line to cash/bank.</description></item>
/// <item><term>Journal</term><description>No cash/bank lines - non-cash adjustments only.</description></item>
/// <item><term>Sales</term><description>At least one credit line to a Sales Accounts ledger.</description></item>
/// <item><term>Purchase</term><description>At least one debit line to a Purchase Accounts ledger.</description></item>
/// <item><term>Credit Note</term><description>At least one debit line to a Sales Accounts ledger (sales return).</description></item>
/// <item><term>Debit Note</term><description>At least one credit line to a Purchase Accounts ledger (purchase return).</description></item>
/// </list>
/// </summary>
public static class VoucherTypeRules
{
    public static void Validate(Voucher voucher, IReadOnlyDictionary<Guid, LedgerTraits> traits)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(traits);
        if (voucher.IsReversal)
        {
            return;
        }

        LedgerTraits TraitsOf(VoucherLine line) =>
            traits.TryGetValue(line.LedgerId, out var t)
                ? t
                : throw new DomainException("voucher.ledger_not_found", $"Line {line.LineNo}: the ledger was not found.");

        bool Any(DrCr side, LedgerTraits trait) => voucher.Lines.Any(l => l.Side == side && TraitsOf(l).HasFlag(trait));

        foreach (var line in voucher.Lines)
        {
            _ = TraitsOf(line);
        }

        switch (voucher.BaseType)
        {
            case VoucherBaseType.Contra:
                var nonCash = voucher.Lines.FirstOrDefault(l => !TraitsOf(l).HasFlag(LedgerTraits.CashOrBank));
                if (nonCash is not null)
                {
                    throw new DomainException("voucher.contra_cash_bank_only", $"Line {nonCash.LineNo}: a Contra voucher can only use cash and bank ledgers.");
                }

                break;
            case VoucherBaseType.Payment:
                if (!Any(DrCr.Credit, LedgerTraits.CashOrBank))
                {
                    throw new DomainException("voucher.payment_needs_cash_bank", "A Payment voucher must credit a cash or bank ledger.");
                }

                break;
            case VoucherBaseType.Receipt:
                if (!Any(DrCr.Debit, LedgerTraits.CashOrBank))
                {
                    throw new DomainException("voucher.receipt_needs_cash_bank", "A Receipt voucher must debit a cash or bank ledger.");
                }

                break;
            case VoucherBaseType.Journal:
                var cash = voucher.Lines.FirstOrDefault(l => TraitsOf(l).HasFlag(LedgerTraits.CashOrBank));
                if (cash is not null)
                {
                    throw new DomainException("voucher.journal_no_cash_bank", $"Line {cash.LineNo}: use Payment, Receipt or Contra for cash and bank ledgers.");
                }

                break;
            case VoucherBaseType.Sales:
                if (!Any(DrCr.Credit, LedgerTraits.SalesAccount))
                {
                    throw new DomainException("voucher.sales_needs_sales_ledger", "A Sales voucher must credit a ledger under Sales Accounts.");
                }

                break;
            case VoucherBaseType.Purchase:
                if (!Any(DrCr.Debit, LedgerTraits.PurchaseAccount))
                {
                    throw new DomainException("voucher.purchase_needs_purchase_ledger", "A Purchase voucher must debit a ledger under Purchase Accounts.");
                }

                break;
            case VoucherBaseType.CreditNote:
                if (!Any(DrCr.Debit, LedgerTraits.SalesAccount))
                {
                    throw new DomainException("voucher.credit_note_needs_sales_ledger", "A Credit Note must debit a ledger under Sales Accounts.");
                }

                break;
            case VoucherBaseType.DebitNote:
                if (!Any(DrCr.Credit, LedgerTraits.PurchaseAccount))
                {
                    throw new DomainException("voucher.debit_note_needs_purchase_ledger", "A Debit Note must credit a ledger under Purchase Accounts.");
                }

                break;
            default:
                throw new DomainException("voucher.type_invalid", "The voucher type is not valid.");
        }
    }
}
