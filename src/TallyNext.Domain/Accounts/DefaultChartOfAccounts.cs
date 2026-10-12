using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Domain.Accounts;

/// <summary>
/// The predefined chart of accounts created with every company. The structure follows the conventional
/// Indian small-business classification (primary groups with their customary sub-groups).
/// </summary>
public static class DefaultChartOfAccounts
{
    private sealed record GroupSpec(string Key, string Name, string? ParentKey, AccountNature Nature, bool AffectsGrossProfit);

    private static readonly GroupSpec[] Groups =
    [
        new(SystemGroupKeys.CapitalAccount, "Capital Account", null, AccountNature.Liability, false),
        new(SystemGroupKeys.ReservesAndSurplus, "Reserves & Surplus", SystemGroupKeys.CapitalAccount, AccountNature.Liability, false),
        new(SystemGroupKeys.LoansLiability, "Loans (Liability)", null, AccountNature.Liability, false),
        new(SystemGroupKeys.BankOverdraft, "Bank OD A/c", SystemGroupKeys.LoansLiability, AccountNature.Liability, false),
        new(SystemGroupKeys.SecuredLoans, "Secured Loans", SystemGroupKeys.LoansLiability, AccountNature.Liability, false),
        new(SystemGroupKeys.UnsecuredLoans, "Unsecured Loans", SystemGroupKeys.LoansLiability, AccountNature.Liability, false),
        new(SystemGroupKeys.CurrentLiabilities, "Current Liabilities", null, AccountNature.Liability, false),
        new(SystemGroupKeys.DutiesAndTaxes, "Duties & Taxes", SystemGroupKeys.CurrentLiabilities, AccountNature.Liability, false),
        new(SystemGroupKeys.Provisions, "Provisions", SystemGroupKeys.CurrentLiabilities, AccountNature.Liability, false),
        new(SystemGroupKeys.SundryCreditors, "Sundry Creditors", SystemGroupKeys.CurrentLiabilities, AccountNature.Liability, false),
        new(SystemGroupKeys.Suspense, "Suspense A/c", null, AccountNature.Liability, false),
        new(SystemGroupKeys.BranchDivisions, "Branch / Divisions", null, AccountNature.Liability, false),
        new(SystemGroupKeys.FixedAssets, "Fixed Assets", null, AccountNature.Asset, false),
        new(SystemGroupKeys.Investments, "Investments", null, AccountNature.Asset, false),
        new(SystemGroupKeys.CurrentAssets, "Current Assets", null, AccountNature.Asset, false),
        new(SystemGroupKeys.BankAccounts, "Bank Accounts", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.CashInHand, "Cash-in-Hand", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.Deposits, "Deposits (Asset)", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.LoansAndAdvancesAsset, "Loans & Advances (Asset)", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.StockInHand, "Stock-in-Hand", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.SundryDebtors, "Sundry Debtors", SystemGroupKeys.CurrentAssets, AccountNature.Asset, false),
        new(SystemGroupKeys.MiscExpensesAsset, "Misc. Expenses (Asset)", null, AccountNature.Asset, false),
        new(SystemGroupKeys.SalesAccounts, "Sales Accounts", null, AccountNature.Income, true),
        new(SystemGroupKeys.DirectIncomes, "Direct Incomes", null, AccountNature.Income, true),
        new(SystemGroupKeys.IndirectIncomes, "Indirect Incomes", null, AccountNature.Income, false),
        new(SystemGroupKeys.PurchaseAccounts, "Purchase Accounts", null, AccountNature.Expense, true),
        new(SystemGroupKeys.DirectExpenses, "Direct Expenses", null, AccountNature.Expense, true),
        new(SystemGroupKeys.IndirectExpenses, "Indirect Expenses", null, AccountNature.Expense, false),
    ];

    private static readonly (VoucherBaseType Type, string Name, string Abbreviation)[] VoucherTypes =
    [
        (VoucherBaseType.Contra, "Contra", "Ctr"),
        (VoucherBaseType.Payment, "Payment", "Pmt"),
        (VoucherBaseType.Receipt, "Receipt", "Rcpt"),
        (VoucherBaseType.Journal, "Journal", "Jrnl"),
        (VoucherBaseType.Sales, "Sales", "Sale"),
        (VoucherBaseType.Purchase, "Purchase", "Purc"),
        (VoucherBaseType.CreditNote, "Credit Note", "CrNt"),
        (VoucherBaseType.DebitNote, "Debit Note", "DrNt"),
    ];

    /// <summary>Builds the default groups (parents before children), ledgers and voucher types for a new company.</summary>
    public static (IReadOnlyList<AccountGroup> Groups, IReadOnlyList<Ledger> Ledgers, IReadOnlyList<VoucherType> VoucherTypes) Build(Guid companyId)
    {
        var byKey = new Dictionary<string, AccountGroup>(StringComparer.Ordinal);
        var groups = new List<AccountGroup>(Groups.Length);
        var order = 0;
        foreach (var spec in Groups)
        {
            order += 10;
            var group = spec.ParentKey is null
                ? AccountGroup.CreatePrimary(Ids.New(), companyId, spec.Name, spec.Nature, spec.AffectsGrossProfit, spec.Key, order)
                : AccountGroup.CreateSubGroup(Ids.New(), byKey[spec.ParentKey], spec.Name, spec.Key, order);
            byKey[spec.Key] = group;
            groups.Add(group);
        }

        var ledgers = new List<Ledger>
        {
            Ledger.Create(Ids.New(), byKey[SystemGroupKeys.CashInHand], "Cash", Money.Zero, DrCr.Debit, systemKey: SystemLedgerKeys.Cash),
            Ledger.Create(Ids.New(), byKey[SystemGroupKeys.ReservesAndSurplus], "Profit & Loss A/c", Money.Zero, DrCr.Credit, systemKey: SystemLedgerKeys.ProfitAndLoss),
        };

        var voucherTypes = VoucherTypes
            .Select(v => VoucherType.CreateSystem(Ids.New(), companyId, v.Type, v.Name, v.Abbreviation))
            .ToList();

        return (groups, ledgers, voucherTypes);
    }
}
