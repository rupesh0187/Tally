using TallyNext.Domain.Common;

namespace TallyNext.Application.Common;

/// <summary>Conversions between persisted minor units and presentation decimals.</summary>
public static class Amounts
{
    /// <summary>Exact conversion of a minor-unit total (which may exceed the single-amount limit) to a decimal.</summary>
    public static decimal ToDecimal(long minor) => decimal.Divide(minor, Money.MinorUnitsPerMajor);

    public static decimal DebitPart(long signedMinor) => signedMinor > 0 ? ToDecimal(signedMinor) : 0m;

    public static decimal CreditPart(long signedMinor) => signedMinor < 0 ? ToDecimal(-signedMinor) : 0m;

    /// <summary>Converts user input, rejecting more than two decimal places rather than rounding it.</summary>
    public static Money Parse(decimal amount, string field)
    {
        try
        {
            return Money.FromDecimal(amount);
        }
        catch (DomainException ex)
        {
            throw new ValidationFailedException(ex.Code, ex.Message, new Dictionary<string, string[]> { [field] = [ex.Message] });
        }
    }
}
