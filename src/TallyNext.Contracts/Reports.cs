using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Contracts.Reports;

// Sign convention (see ARCHITECTURE.md, "Accounting sign convention"):
// internally a signed balance is (debits - credits). Reports present balances in separate Dr / Cr columns,
// each non-negative, so the presentation never relies on the reader interpreting a minus sign.

public sealed record DayBookRow(
    Guid VoucherId,
    DateOnly Date,
    string? Number,
    string VoucherTypeName,
    VoucherBaseType BaseType,
    VoucherStatus Status,
    string Particulars,
    decimal Debit,
    decimal Credit,
    string? Narration,
    bool IsReversal);

public sealed record DayBookReport(DateOnly From, DateOnly To, PagedResult<DayBookRow> Rows, decimal PeriodTotal);

public sealed record LedgerReportRow(
    Guid VoucherId,
    Guid LineId,
    DateOnly Date,
    string? Number,
    string VoucherTypeName,
    string Particulars,
    string? Narration,
    decimal Debit,
    decimal Credit,
    decimal BalanceDebit,
    decimal BalanceCredit);

public sealed record LedgerReport(
    Guid LedgerId,
    string LedgerName,
    string GroupName,
    DateOnly From,
    DateOnly To,
    decimal OpeningDebit,
    decimal OpeningCredit,
    decimal PeriodDebit,
    decimal PeriodCredit,
    decimal ClosingDebit,
    decimal ClosingCredit,
    PagedResult<LedgerReportRow> Rows);

public sealed record TrialBalanceRow(
    Guid Id,
    string Name,
    bool IsGroup,
    Guid? ParentGroupId,
    int Depth,
    AccountNature Nature,
    decimal OpeningDebit,
    decimal OpeningCredit,
    decimal PeriodDebit,
    decimal PeriodCredit,
    decimal ClosingDebit,
    decimal ClosingCredit);

public sealed record TrialBalanceReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<TrialBalanceRow> Rows,
    decimal TotalOpeningDebit,
    decimal TotalOpeningCredit,
    decimal TotalPeriodDebit,
    decimal TotalPeriodCredit,
    decimal TotalClosingDebit,
    decimal TotalClosingCredit,
    decimal OpeningDifference)
{
    /// <summary>Closing debits equal closing credits once any opening-balance difference is accounted for.</summary>
    public bool IsBalanced => TotalClosingDebit - TotalClosingCredit == OpeningDifference;
}

public sealed record StatementLine(string Name, Guid? GroupId, Guid? LedgerId, int Depth, decimal Amount);

public sealed record StatementSection(string Title, IReadOnlyList<StatementLine> Lines, decimal Total);

/// <param name="GrossProfit">Trading income minus direct costs. Negative is a gross loss.</param>
/// <param name="NetProfit">Gross profit plus indirect incomes minus indirect expenses. Negative is a net loss.</param>
public sealed record ProfitAndLossReport(
    DateOnly From,
    DateOnly To,
    StatementSection TradingIncome,
    StatementSection DirectCosts,
    decimal GrossProfit,
    StatementSection IndirectIncomes,
    StatementSection IndirectExpenses,
    decimal NetProfit,
    bool ExcludesInventory,
    decimal OpeningStock = 0,
    decimal ClosingStock = 0);

public sealed record StockSummaryRow(
    Guid StockItemId,
    string Name,
    string UnitSymbol,
    decimal OpeningQuantity,
    decimal OpeningValue,
    decimal InwardQuantity,
    decimal InwardValue,
    decimal OutwardQuantity,
    decimal OutwardCost,
    decimal ClosingQuantity,
    decimal ClosingValue,
    bool WentNegative,
    IReadOnlyList<GodownQuantity> ByGodown);

public sealed record GodownQuantity(Guid GodownId, string GodownName, decimal Quantity);

public sealed record StockSummaryReport(DateOnly From, DateOnly To, IReadOnlyList<StockSummaryRow> Rows, decimal TotalOpeningValue, decimal TotalClosingValue);

/// <summary>One rate bucket of the GST summary. Amounts are net of credit/debit notes and reversals.</summary>
public sealed record GstSummaryRow(Taxability Taxability, decimal GstPercent, SupplyNature SupplyNature, decimal Taxable, decimal Cgst, decimal StateTax, decimal Igst, decimal Cess);

/// <summary>
/// Period summary of outward supplies (sales less credit notes) and inward supplies (purchases less debit notes)
/// with the tax on each. An aid for preparing returns; it is not itself a GST return.
/// </summary>
public sealed record GstSummaryReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<GstSummaryRow> Outward,
    IReadOnlyList<GstSummaryRow> Inward,
    decimal OutputTax,
    decimal InputTax,
    decimal NetPayable);

/// <param name="ProfitAndLossBalance">
/// Accumulated profit (positive) or loss (negative) through <see cref="AsAt"/>: the Profit &amp; Loss ledger's balance
/// plus the net of every income and expense posting up to the date. Shown once, inside Liabilities.
/// </param>
/// <param name="OpeningDifference">
/// Debit-minus-credit total of all opening balances. Non-zero means the opening balances entered do not agree;
/// it is shown as a separate line so the statement still balances visibly.
/// </param>
public sealed record BalanceSheetReport(
    DateOnly AsAt,
    StatementSection Liabilities,
    StatementSection Assets,
    decimal ProfitAndLossBalance,
    decimal CurrentPeriodProfit,
    decimal OpeningDifference,
    decimal TotalLiabilities,
    decimal TotalAssets);

public enum OutstandingKind : byte
{
    /// <summary>Bill-wise ledgers under asset groups (customers): amounts due to the company.</summary>
    Receivables = 1,

    /// <summary>Bill-wise ledgers under liability groups (suppliers): amounts the company owes.</summary>
    Payables = 2,
}

/// <summary>
/// One bill with a pending amount. Amounts are signed in the report's direction: positive = due to the company for
/// receivables and owed by the company for payables; negative = the opposite (an advance or an over-settlement).
/// </summary>
/// <param name="OverdueDays">Days past the due date (or the bill date when there is none); negative = not yet due.</param>
public sealed record OutstandingBill(
    Guid LedgerId,
    string LedgerName,
    string BillName,
    DateOnly BillDate,
    DateOnly? DueDate,
    decimal Original,
    decimal Pending,
    int OverdueDays,
    bool IsAdvance);

/// <param name="Balance">Ledger balance as at the report date = <paramref name="BillsPending"/> + <paramref name="OnAccount"/>.</param>
public sealed record OutstandingLedger(Guid LedgerId, string LedgerName, string GroupName, decimal BillsPending, decimal OnAccount, decimal Balance);

/// <summary>Pending bill amounts by age: not due, 0-30, 31-60, 61-90 and over 90 days past due.</summary>
public sealed record AgeingBucket(string Label, decimal Amount);

public sealed record OutstandingReport(
    DateOnly AsAt,
    OutstandingKind Kind,
    IReadOnlyList<OutstandingBill> Bills,
    IReadOnlyList<OutstandingLedger> Ledgers,
    IReadOnlyList<AgeingBucket> Ageing,
    decimal TotalBillsPending,
    decimal TotalOnAccount,
    decimal Total);

/// <summary>A pending bill of one ledger, for choosing what a payment or receipt settles.</summary>
public sealed record PendingBillDto(string BillName, DateOnly BillDate, DateOnly? DueDate, decimal Amount, DrCr Side);
