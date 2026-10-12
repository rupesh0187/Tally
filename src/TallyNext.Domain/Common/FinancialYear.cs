namespace TallyNext.Domain.Common;

/// <summary>
/// A twelve-month accounting year identified by its first day. Indian companies default to
/// 1 April - 31 March, but the start month is configurable per company.
/// </summary>
public readonly record struct FinancialYear
{
    public FinancialYear(DateOnly start)
    {
        if (start.Day != 1)
        {
            throw new DomainException("financial_year.invalid_start", "A financial year must start on the first day of a month.");
        }

        Start = start;
    }

    public DateOnly Start { get; }

    public DateOnly End => Start.AddYears(1).AddDays(-1);

    /// <summary>"2026-27" for April-March years, "2026" for calendar years.</summary>
    public string Label => Start.Month == 1
        ? Start.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{Start.Year}-{(Start.Year + 1) % 100:00}";

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public static FinancialYear Containing(DateOnly date, int startMonth)
    {
        if (startMonth is < 1 or > 12)
        {
            throw new DomainException("financial_year.invalid_month", "The financial year start month must be between 1 and 12.");
        }

        var year = date.Month >= startMonth ? date.Year : date.Year - 1;
        return new FinancialYear(new DateOnly(year, startMonth, 1));
    }

    public override string ToString() => Label;
}
