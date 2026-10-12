namespace TallyNext.Domain.Common;

/// <summary>
/// Side of a double-entry posting.
/// Sign convention used throughout the system: a signed balance is (debits - credits);
/// positive means a debit balance ("Dr"), negative means a credit balance ("Cr").
/// </summary>
public enum DrCr : byte
{
    Debit = 1,
    Credit = 2,
}

public static class DrCrExtensions
{
    public static DrCr Opposite(this DrCr side) => side == DrCr.Debit ? DrCr.Credit : DrCr.Debit;

    public static int Sign(this DrCr side) => side == DrCr.Debit ? 1 : -1;

    public static string ToShortLabel(this DrCr side) => side == DrCr.Debit ? "Dr" : "Cr";

    public static bool IsDefined(this DrCr side) => side is DrCr.Debit or DrCr.Credit;
}
