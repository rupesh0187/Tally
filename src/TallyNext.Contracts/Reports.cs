using TallyNext.Domain.Accounts;
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
    bool ExcludesInventory);

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
