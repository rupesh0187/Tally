namespace TallyNext.Domain.Accounts;

/// <summary>
/// Stable identifiers for predefined groups and ledgers. Business rules (for example "a Contra voucher may
/// only use cash and bank ledgers") are keyed on these, never on display names, so renaming is safe.
/// </summary>
public static class SystemGroupKeys
{
    public const string CapitalAccount = "capital-account";
    public const string ReservesAndSurplus = "reserves-surplus";
    public const string CurrentAssets = "current-assets";
    public const string BankAccounts = "bank-accounts";
    public const string CashInHand = "cash-in-hand";
    public const string Deposits = "deposits-asset";
    public const string LoansAndAdvancesAsset = "loans-advances-asset";
    public const string StockInHand = "stock-in-hand";
    public const string SundryDebtors = "sundry-debtors";
    public const string CurrentLiabilities = "current-liabilities";
    public const string DutiesAndTaxes = "duties-taxes";
    public const string Provisions = "provisions";
    public const string SundryCreditors = "sundry-creditors";
    public const string FixedAssets = "fixed-assets";
    public const string Investments = "investments";
    public const string LoansLiability = "loans-liability";
    public const string BankOverdraft = "bank-od";
    public const string SecuredLoans = "secured-loans";
    public const string UnsecuredLoans = "unsecured-loans";
    public const string Suspense = "suspense";
    public const string MiscExpensesAsset = "misc-expenses-asset";
    public const string BranchDivisions = "branch-divisions";
    public const string DirectExpenses = "direct-expenses";
    public const string DirectIncomes = "direct-incomes";
    public const string IndirectExpenses = "indirect-expenses";
    public const string IndirectIncomes = "indirect-incomes";
    public const string PurchaseAccounts = "purchase-accounts";
    public const string SalesAccounts = "sales-accounts";

    /// <summary>Groups whose ledgers are treated as cash or bank (Contra/Payment/Receipt rules, Cash and Bank Book).</summary>
    public static IReadOnlySet<string> CashOrBank { get; } =
        new HashSet<string>(StringComparer.Ordinal) { CashInHand, BankAccounts, BankOverdraft };
}

public static class SystemLedgerKeys
{
    public const string Cash = "cash";

    /// <summary>
    /// Accumulated profit/loss of earlier periods. The balance sheet shows this ledger's balance plus the
    /// computed profit of all income/expense postings up to the report date (no closing entries required).
    /// </summary>
    public const string ProfitAndLoss = "profit-and-loss";
}
