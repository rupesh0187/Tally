namespace TallyNext.Domain.Accounts;

/// <summary>
/// Fundamental classification inherited from a group's primary (top-level) ancestor.
/// Capital/equity is treated as <see cref="Liability"/> nature (it sits on the liabilities side of the
/// balance sheet); income and expense groups feed the profit and loss account.
/// </summary>
public enum AccountNature : byte
{
    Asset = 1,
    Liability = 2,
    Income = 3,
    Expense = 4,
}

public static class AccountNatureExtensions
{
    public static bool IsBalanceSheet(this AccountNature nature) => nature is AccountNature.Asset or AccountNature.Liability;

    public static bool IsProfitAndLoss(this AccountNature nature) => nature is AccountNature.Income or AccountNature.Expense;
}
