using System.Globalization;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;

namespace TallyNext.Domain.Tax;

/// <summary>Which GST head a duty ledger accumulates.</summary>
public enum GstTaxType : byte
{
    None = 0,
    Cgst = 1,
    Sgst = 2,
    Utgst = 3,
    Igst = 4,
    Cess = 5,
}

public enum Taxability : byte
{
    Taxable = 1,
    Exempt = 2,
    NilRated = 3,
    NonGst = 4,
}

/// <summary>How an invoice is taxed, decided by the place of supply.</summary>
public enum SupplyNature : byte
{
    /// <summary>Supplier and place of supply in the same State: CGST + SGST.</summary>
    IntraState = 1,

    /// <summary>Same Union Territory without legislature: CGST + UTGST.</summary>
    IntraStateUnionTerritory = 2,

    /// <summary>Different States/UTs, or supplies to/by SEZ units and exports: IGST.</summary>
    InterState = 3,
}

/// <summary>
/// One entry of an effective-dated GST rate schedule. Rates are integer thousandths of a percent
/// (18% = 18000, 0.25% = 250) so they are exact on every database.
/// </summary>
public sealed record GstRateEntry(DateOnly EffectiveFrom, Taxability Taxability, int IgstMilliPercent, int CessMilliPercent, string? HsnSac)
{
    public const int MaxMilliPercent = 100_000;

    public decimal IgstPercent => IgstMilliPercent / 1000m;

    public decimal CessPercent => CessMilliPercent / 1000m;

    public static int ToMilliPercent(decimal percent, string field)
    {
        var scaled = percent * 1000m;
        if (scaled != decimal.Truncate(scaled) || scaled < 0 || scaled > MaxMilliPercent)
        {
            throw new DomainException($"gst.{field}_invalid", $"The {field} must be between 0 and 100 with at most three decimal places.");
        }

        return (int)scaled;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Taxability))
        {
            throw new DomainException("gst.taxability_invalid", "The taxability is not valid.");
        }

        if (IgstMilliPercent is < 0 or > MaxMilliPercent || CessMilliPercent is < 0 or > MaxMilliPercent)
        {
            throw new DomainException("gst.rate_invalid", "GST rates must be between 0% and 100%.");
        }

        if (Taxability != Taxability.Taxable && (IgstMilliPercent != 0 || CessMilliPercent != 0))
        {
            throw new DomainException("gst.rate_for_untaxed", "Exempt, nil-rated and non-GST supplies carry no tax rate.");
        }

        if (HsnSac is { } code && (code.Length is < 4 or > 8 || !code.All(char.IsAsciiDigit)))
        {
            throw new DomainException("gst.hsn_invalid", "An HSN/SAC code has 4 to 8 digits.");
        }
    }

    public override string ToString() => Taxability == Taxability.Taxable
        ? string.Create(CultureInfo.InvariantCulture, $"{IgstPercent:0.###}%")
        : Taxability.ToString();
}

/// <summary>Effective-dated rate schedules (rates change by notification; history must be kept).</summary>
public static class GstRateSchedule
{
    public static IReadOnlyList<GstRateEntry> Normalize(IEnumerable<GstRateEntry>? entries)
    {
        var list = (entries ?? []).Select(e => e with { HsnSac = string.IsNullOrWhiteSpace(e.HsnSac) ? null : e.HsnSac.Trim() })
            .OrderBy(e => e.EffectiveFrom)
            .ToList();
        foreach (var entry in list)
        {
            entry.Validate();
        }

        if (list.GroupBy(e => e.EffectiveFrom).Any(g => g.Count() > 1))
        {
            throw new DomainException("gst.duplicate_effective_date", "Two rates cannot start on the same date.");
        }

        return list;
    }

    /// <summary>The entry in force on <paramref name="date"/>, or null when none applies yet.</summary>
    public static GstRateEntry? Resolve(IReadOnlyList<GstRateEntry> entries, DateOnly date)
    {
        GstRateEntry? found = null;
        foreach (var entry in entries)
        {
            if (entry.EffectiveFrom <= date && (found is null || entry.EffectiveFrom >= found.EffectiveFrom))
            {
                found = entry;
            }
        }

        return found;
    }
}

/// <summary>
/// Default place-of-supply decision (IGST Act ss. 7-8). Exports and supplies to or by SEZ units are always
/// inter-State. Otherwise the supply is intra-State only when the supplier's location and the place of supply are
/// the same State/UT. Special place-of-supply rules (immovable property, events, transport, OIDAR, ...) are not
/// inferred: the user sets the place of supply on the invoice for those cases.
/// </summary>
public static class PlaceOfSupply
{
    public static SupplyNature Determine(string? supplierStateCode, string? placeOfSupplyStateCode, GstRegistrationType counterpartyRegistration)
    {
        if (counterpartyRegistration is GstRegistrationType.Overseas or GstRegistrationType.SpecialEconomicZone)
        {
            return SupplyNature.InterState;
        }

        if (string.IsNullOrEmpty(supplierStateCode))
        {
            throw new DomainException("gst.state_missing", "Set the State of the company (and of the party) to determine the place of supply.");
        }

        var place = string.IsNullOrEmpty(placeOfSupplyStateCode) ? supplierStateCode : placeOfSupplyStateCode;
        if (!string.Equals(place, supplierStateCode, StringComparison.Ordinal))
        {
            return SupplyNature.InterState;
        }

        return IndianStates.Find(place)?.IsUnionTerritoryWithoutLegislature == true
            ? SupplyNature.IntraStateUnionTerritory
            : SupplyNature.IntraState;
    }
}

/// <summary>Tax on one invoice line, each head rounded to the paisa.</summary>
public readonly record struct LineTax(Money Cgst, Money StateTax, Money Igst, Money Cess)
{
    public Money Total => Cgst + StateTax + Igst + Cess;
}

/// <summary>
/// GST arithmetic. Rounding: per line and per tax head, half away from zero, to the paisa. CGST and SGST/UTGST are
/// each half of the rate computed separately, so their sum can differ from the IGST on the same value by one paisa.
/// </summary>
public static class GstCalculator
{
    public static LineTax Compute(Money taxableValue, GstRateEntry rate, SupplyNature nature)
    {
        ArgumentNullException.ThrowIfNull(rate);
        if (rate.Taxability != Taxability.Taxable)
        {
            return default;
        }

        var value = taxableValue.ToDecimal();
        var cess = Money.Round(value * rate.CessMilliPercent / 100_000m);
        if (nature == SupplyNature.InterState)
        {
            return new LineTax(Money.Zero, Money.Zero, Money.Round(value * rate.IgstMilliPercent / 100_000m), cess);
        }

        var half = Money.Round(value * rate.IgstMilliPercent / 200_000m);
        return new LineTax(half, half, Money.Zero, cess);
    }
}
