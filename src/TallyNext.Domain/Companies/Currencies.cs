namespace TallyNext.Domain.Companies;

public sealed record CurrencyInfo(string Code, string Symbol, string Name);

/// <summary>
/// Currencies a company may use as its base currency. Only currencies with exactly two minor-unit
/// digits are supported because amounts are stored as integer hundredths (see <see cref="Common.Money"/>).
/// Three-decimal currencies (KWD, BHD, OMR) are intentionally excluded until per-currency scale is implemented.
/// </summary>
public static class Currencies
{
    public static IReadOnlyList<CurrencyInfo> Supported { get; } =
    [
        new("INR", "₹", "Indian Rupee"),
        new("USD", "$", "US Dollar"),
        new("EUR", "€", "Euro"),
        new("GBP", "£", "Pound Sterling"),
        new("AED", "AED", "UAE Dirham"),
        new("SGD", "S$", "Singapore Dollar"),
        new("AUD", "A$", "Australian Dollar"),
        new("CAD", "C$", "Canadian Dollar"),
        new("NPR", "Rs", "Nepalese Rupee"),
        new("LKR", "Rs", "Sri Lankan Rupee"),
        new("BDT", "Tk", "Bangladeshi Taka"),
    ];

    public static CurrencyInfo? Find(string? code) =>
        Supported.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
}
