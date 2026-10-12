using System.Globalization;

namespace TallyNext.Domain.Common;

/// <summary>
/// Exact monetary amount stored as a whole number of minor units (paise for INR).
/// </summary>
/// <remarks>
/// <para>Money strategy (see DATABASE_DESIGN.md, "Money"):</para>
/// <list type="bullet">
/// <item>All supported company currencies have exactly two minor-unit digits; amounts are held as
/// <see cref="long"/> minor units in memory and as 64-bit integers in both SQLite and PostgreSQL.
/// Integer storage gives exact, fast SUMs on both providers (SQLite has no exact decimal type).</item>
/// <item>Floating-point types are never used for money.</item>
/// <item>User input is converted with <see cref="FromDecimal"/>, which rejects more than two decimal
/// places instead of silently rounding. Computed values (tax, allocation) are rounded explicitly with
/// <see cref="Round"/> using a documented <see cref="MidpointRounding"/> mode.</item>
/// <item>The absolute value of any single amount is limited to <see cref="MaxAbsMinor"/> so that sums of
/// many amounts cannot overflow 64 bits in practice; arithmetic is checked regardless.</item>
/// </list>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    public const int DecimalPlaces = 2;
    public const long MinorUnitsPerMajor = 100;

    /// <summary>10^15 minor units (10 trillion rupees) - the largest single amount accepted.</summary>
    public const long MaxAbsMinor = 1_000_000_000_000_000;

    private Money(long minor) => Minor = minor;

    public long Minor { get; }

    public static Money Zero => default;

    public bool IsZero => Minor == 0;

    public bool IsPositive => Minor > 0;

    public bool IsNegative => Minor < 0;

    public static Money FromMinor(long minor)
    {
        if (minor > MaxAbsMinor || minor < -MaxAbsMinor)
        {
            throw new DomainException("money.out_of_range", "The amount is outside the supported range.");
        }

        return new Money(minor);
    }

    /// <summary>Exact conversion. Throws if <paramref name="amount"/> has more than two decimal places.</summary>
    public static Money FromDecimal(decimal amount)
    {
        decimal scaled;
        try
        {
            scaled = amount * MinorUnitsPerMajor;
        }
        catch (OverflowException)
        {
            throw new DomainException("money.out_of_range", "The amount is outside the supported range.");
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw new DomainException(
                "money.precision",
                $"Amounts may have at most {DecimalPlaces} decimal places (got {amount.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (scaled > MaxAbsMinor || scaled < -MaxAbsMinor)
        {
            throw new DomainException("money.out_of_range", "The amount is outside the supported range.");
        }

        return new Money((long)scaled);
    }

    /// <summary>
    /// Rounds a computed value to the currency precision. The default, <see cref="MidpointRounding.AwayFromZero"/>,
    /// matches common Indian commercial practice (0.005 rounds to 0.01).
    /// </summary>
    public static Money Round(decimal amount, MidpointRounding mode = MidpointRounding.AwayFromZero)
        => FromDecimal(Math.Round(amount, DecimalPlaces, mode));

    public decimal ToDecimal() => decimal.Divide(Minor, MinorUnitsPerMajor);

    public Money Abs() => Minor < 0 ? new Money(-Minor) : this;

    public Money Negate() => new(-Minor);

    public static Money operator +(Money left, Money right) => FromMinorChecked(checked(left.Minor + right.Minor));

    public static Money operator -(Money left, Money right) => FromMinorChecked(checked(left.Minor - right.Minor));

    public static Money operator -(Money value) => new(-value.Minor);

    public static bool operator <(Money left, Money right) => left.Minor < right.Minor;

    public static bool operator >(Money left, Money right) => left.Minor > right.Minor;

    public static bool operator <=(Money left, Money right) => left.Minor <= right.Minor;

    public static bool operator >=(Money left, Money right) => left.Minor >= right.Minor;

    public int CompareTo(Money other) => Minor.CompareTo(other.Minor);

    public static Money Sum(IEnumerable<Money> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total = checked(total + value.Minor);
        }

        return new Money(total);
    }

    public override string ToString() => ToDecimal().ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Sums may legitimately exceed the single-amount limit; only overflow is an error.</summary>
    private static Money FromMinorChecked(long minor) => new(minor);
}
