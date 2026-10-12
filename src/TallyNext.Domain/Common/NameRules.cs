using System.Text;

namespace TallyNext.Domain.Common;

/// <summary>
/// Normalization and validation for master names (ledgers, groups, voucher types).
/// Uniqueness is enforced on the normalized form so "HDFC  Bank" and "hdfc bank" collide.
/// </summary>
public static class NameRules
{
    public const int MaxLength = 100;

    public static string Clean(string? value, string field = "name")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{field}.required", $"The {field} is required.");
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch))
            {
                throw new DomainException($"{field}.invalid", $"The {field} contains control characters.");
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            builder.Append(ch);
        }

        var cleaned = builder.ToString();
        if (cleaned.Length > MaxLength)
        {
            throw new DomainException($"{field}.too_long", $"The {field} must be at most {MaxLength} characters.");
        }

        return cleaned;
    }

    public static string Normalize(string cleanedName) => cleanedName.ToUpperInvariant();

    public static string? CleanOptional(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{field}.too_long", $"The {field} must be at most {maxLength} characters.");
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')
            {
                throw new DomainException($"{field}.invalid", $"The {field} contains control characters.");
            }
        }

        return trimmed;
    }
}
