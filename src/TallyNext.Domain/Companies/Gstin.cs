using System.Text.RegularExpressions;

namespace TallyNext.Domain.Companies;

/// <summary>
/// Structural validation of a 15-character GSTIN for regular taxpayers:
/// 2-digit state code, 10-character PAN, entity number, 'Z', and a mod-36 check character.
/// </summary>
/// <remarks>
/// This verifies format and checksum only. It does not verify that the registration is active,
/// and special registrations (UN bodies, non-resident OIDAR, TDS/TCS deductors) use other formats.
/// </remarks>
public static partial class Gstin
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    public static bool IsValid(string? value)
    {
        var gstin = Normalize(value);
        if (gstin is null || !Pattern().IsMatch(gstin))
        {
            return false;
        }

        return ComputeCheckCharacter(gstin.AsSpan(0, 14)) == gstin[14];
    }

    public static string StateCodeOf(string gstin) => gstin[..2];

    public static string PanOf(string gstin) => gstin.Substring(2, 10);

    public static char ComputeCheckCharacter(ReadOnlySpan<char> first14)
    {
        if (first14.Length != 14)
        {
            throw new ArgumentException("Exactly 14 characters are required.", nameof(first14));
        }

        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var value = Alphabet.IndexOf(first14[i]);
            if (value < 0)
            {
                throw new ArgumentException("Invalid GSTIN character.", nameof(first14));
            }

            var product = value * ((i % 2) + 1);
            sum += (product / 36) + (product % 36);
        }

        return Alphabet[(36 - (sum % 36)) % 36];
    }
}
