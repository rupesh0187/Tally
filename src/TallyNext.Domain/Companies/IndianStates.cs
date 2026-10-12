namespace TallyNext.Domain.Companies;

/// <param name="Code">Two-digit GST state code (first two characters of a GSTIN).</param>
/// <param name="IsUnionTerritoryWithoutLegislature">
/// True where UTGST applies instead of SGST for intra-territory supplies.
/// </param>
public sealed record IndianState(string Code, string Name, bool IsUnionTerritoryWithoutLegislature);

/// <summary>GST state and union-territory codes. Retired codes (25, 28) are not offered for new data.</summary>
public static class IndianStates
{
    public static IReadOnlyList<IndianState> All { get; } =
    [
        new("01", "Jammu and Kashmir", false),
        new("02", "Himachal Pradesh", false),
        new("03", "Punjab", false),
        new("04", "Chandigarh", true),
        new("05", "Uttarakhand", false),
        new("06", "Haryana", false),
        new("07", "Delhi", false),
        new("08", "Rajasthan", false),
        new("09", "Uttar Pradesh", false),
        new("10", "Bihar", false),
        new("11", "Sikkim", false),
        new("12", "Arunachal Pradesh", false),
        new("13", "Nagaland", false),
        new("14", "Manipur", false),
        new("15", "Mizoram", false),
        new("16", "Tripura", false),
        new("17", "Meghalaya", false),
        new("18", "Assam", false),
        new("19", "West Bengal", false),
        new("20", "Jharkhand", false),
        new("21", "Odisha", false),
        new("22", "Chhattisgarh", false),
        new("23", "Madhya Pradesh", false),
        new("24", "Gujarat", false),
        new("26", "Dadra and Nagar Haveli and Daman and Diu", true),
        new("27", "Maharashtra", false),
        new("29", "Karnataka", false),
        new("30", "Goa", false),
        new("31", "Lakshadweep", true),
        new("32", "Kerala", false),
        new("33", "Tamil Nadu", false),
        new("34", "Puducherry", false),
        new("35", "Andaman and Nicobar Islands", true),
        new("36", "Telangana", false),
        new("37", "Andhra Pradesh", false),
        new("38", "Ladakh", true),
        new("97", "Other Territory", true),
    ];

    private static readonly Dictionary<string, IndianState> ByCode = All.ToDictionary(s => s.Code, StringComparer.Ordinal);

    public static IndianState? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var state) ? state : null;

    public static bool IsValidCode(string? code) => Find(code) is not null;
}
