using TallyNext.Domain.Common;

namespace TallyNext.Domain.Vouchers;

/// <summary>
/// A voucher number.  Numbers are unique per (company, voucher type, financial year, series).
/// The series code identifies who allocated the number: the server uses <see cref="ServerSeriesCode"/>
/// and every registered device has its own globally unique code assigned at provisioning, so devices can
/// number vouchers while offline without ever colliding.
/// </summary>
public readonly record struct VoucherNumber(string SeriesCode, long Sequence)
{
    public const string ServerSeriesCode = "W";

    public string Display => Format(SeriesCode, Sequence);

    public static string Format(string seriesCode, long sequence) =>
        seriesCode == ServerSeriesCode
            ? sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{seriesCode}/{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static bool IsValidSeriesCode(string? code) =>
        code is { Length: >= 1 and <= 8 } && code.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));
}

/// <summary>
/// Counter for one numbering series. Rows are local to each store (server or device) and are never
/// replicated; allocation happens inside the company write transaction so two concurrent saves cannot
/// obtain the same number, and a unique index on vouchers is the final backstop.
/// </summary>
public sealed class VoucherNumberSeries
{
    private VoucherNumberSeries()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid VoucherTypeId { get; private set; }

    public DateOnly FinancialYearStart { get; private set; }

    public string SeriesCode { get; private set; } = string.Empty;

    public long LastNumber { get; private set; }

    public static VoucherNumberSeries Create(Guid companyId, Guid voucherTypeId, FinancialYear year, string seriesCode)
    {
        if (!VoucherNumber.IsValidSeriesCode(seriesCode))
        {
            throw new DomainException("voucher.series_invalid", "A series code is 1-8 upper-case letters or digits.");
        }

        return new VoucherNumberSeries
        {
            Id = Ids.New(),
            CompanyId = companyId,
            VoucherTypeId = voucherTypeId,
            FinancialYearStart = year.Start,
            SeriesCode = seriesCode,
        };
    }

    public VoucherNumber AllocateNext()
    {
        LastNumber = checked(LastNumber + 1);
        return new VoucherNumber(SeriesCode, LastNumber);
    }

    /// <summary>Moves the counter forward when numbers from this series are discovered elsewhere (e.g. after restore).</summary>
    public void EnsureAtLeast(long sequence)
    {
        if (sequence > LastNumber)
        {
            LastNumber = sequence;
        }
    }
}
