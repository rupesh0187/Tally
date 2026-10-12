using TallyNext.Domain.Common;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Inventory;

/// <summary>
/// Exact quantity and rate conventions. Quantities are integer thousandths of a unit ("milli-units", up to three
/// decimals); rates are integer ten-thousandths of the currency per unit (up to four decimals). Both are 64-bit
/// integers in every database, so stock sums are exact on SQLite and PostgreSQL alike.
/// </summary>
public static class StockMath
{
    public const long QuantityScale = 1000;
    public const long RateScale = 10_000;
    public const long MaxQuantityMilli = 1_000_000_000_000_000;

    public static long ToQuantityMilli(decimal quantity, int unitDecimals)
    {
        var scaled = quantity * QuantityScale;
        if (scaled != decimal.Truncate(scaled) || scaled > MaxQuantityMilli || scaled < -MaxQuantityMilli)
        {
            throw new DomainException("stock.quantity_precision", "Quantities may have at most three decimal places.");
        }

        var allowed = Math.Clamp(unitDecimals, 0, 3);
        var unitStep = (long)Math.Pow(10, 3 - allowed);
        if ((long)scaled % unitStep != 0)
        {
            throw new DomainException("stock.quantity_unit_precision", $"This unit allows {allowed} decimal place(s).");
        }

        return (long)scaled;
    }

    public static decimal ToQuantity(long quantityMilli) => decimal.Divide(quantityMilli, QuantityScale);

    public static long ToRateScaled(decimal rate)
    {
        var scaled = rate * RateScale;
        if (scaled != decimal.Truncate(scaled) || scaled < 0 || scaled > long.MaxValue / 1_000_000)
        {
            throw new DomainException("stock.rate_precision", "Rates may have at most four decimal places and must not be negative.");
        }

        return (long)scaled;
    }

    public static decimal ToRate(long rateScaled) => decimal.Divide(rateScaled, RateScale);

    /// <summary>Line value = quantity x rate x (1 - discount), rounded to the paisa (half away from zero).</summary>
    public static Money LineValue(long quantityMilli, long rateScaled, int discountMilliPercent)
    {
        if (discountMilliPercent is < 0 or > 100_000)
        {
            throw new DomainException("stock.discount_invalid", "The discount must be between 0% and 100%.");
        }

        var gross = ToQuantity(quantityMilli) * ToRate(rateScaled);
        var net = gross * (100_000 - discountMilliPercent) / 100_000m;
        return Money.Round(net);
    }
}

public enum ValuationMethod : byte
{
    /// <summary>Perpetual moving weighted average.</summary>
    WeightedAverage = 1,

    /// <summary>First in, first out.</summary>
    Fifo = 2,
}

public enum StockDirection : byte
{
    None = 0,
    In = 1,
    Out = 2,
}

public sealed class UnitOfMeasure : CompanyScopedEntity
{
    private UnitOfMeasure()
    {
    }

    public string Symbol { get; private set; } = string.Empty;

    public string NormalizedSymbol { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Decimal places allowed in quantities of this unit (0-3).</summary>
    public int DecimalPlaces { get; private set; }

    public static UnitOfMeasure Create(Guid id, Guid companyId, string symbol, string name, int decimalPlaces)
    {
        var unit = new UnitOfMeasure { Id = id, CompanyId = companyId };
        unit.Update(symbol, name, decimalPlaces);
        return unit;
    }

    public void Update(string symbol, string name, int decimalPlaces)
    {
        EnsureNotDeleted();
        var cleanSymbol = NameRules.Clean(symbol, "unit_symbol");
        if (cleanSymbol.Length > 10)
        {
            throw new DomainException("unit_symbol.too_long", "A unit symbol has at most 10 characters.");
        }

        if (decimalPlaces is < 0 or > 3)
        {
            throw new DomainException("unit.decimals_invalid", "A unit allows 0 to 3 decimal places.");
        }

        Symbol = cleanSymbol;
        NormalizedSymbol = NameRules.Normalize(cleanSymbol);
        Name = NameRules.Clean(name, "unit_name");
        DecimalPlaces = decimalPlaces;
    }

    public void MarkDeleted() => IsDeleted = true;

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("unit.deleted", "The unit has been deleted.");
        }
    }
}

public sealed class Godown : CompanyScopedEntity
{
    public const string MainLocationName = "Main Location";

    private Godown()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public static Godown Create(Guid id, Guid companyId, string name, bool isSystem = false)
    {
        var godown = new Godown { Id = id, CompanyId = companyId, IsSystem = isSystem };
        godown.Rename(name);
        return godown;
    }

    public void Rename(string name)
    {
        if (IsDeleted)
        {
            throw new DomainException("godown.deleted", "The godown has been deleted.");
        }

        Name = NameRules.Clean(name, "godown_name");
        NormalizedName = NameRules.Normalize(Name);
    }

    public void MarkDeleted()
    {
        if (IsSystem)
        {
            throw new DomainException("godown.system_readonly", "The main location cannot be deleted.");
        }

        IsDeleted = true;
    }
}

/// <summary>
/// A stock item. Opening quantity and value are as at the company's books-beginning date. The GST rate schedule is
/// effective-dated; invoices copy the rate in force on their date, so later rate changes never alter old invoices.
/// </summary>
public sealed class StockItem : CompanyScopedEntity
{
    private StockItem()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Alias { get; private set; }

    public string? PartNumber { get; private set; }

    public Guid UnitId { get; private set; }

    public ValuationMethod ValuationMethod { get; private set; } = ValuationMethod.WeightedAverage;

    public IReadOnlyList<GstRateEntry> GstRates { get; private set; } = [];

    public long OpeningQuantityMilli { get; private set; }

    public long OpeningValueMinor { get; private set; }

    public Guid? OpeningGodownId { get; private set; }

    public static StockItem Create(
        Guid id,
        Guid companyId,
        string name,
        UnitOfMeasure unit,
        ValuationMethod valuation,
        IEnumerable<GstRateEntry>? gstRates,
        long openingQuantityMilli,
        Money openingValue,
        Godown? openingGodown,
        string? alias = null,
        string? partNumber = null)
    {
        var item = new StockItem { Id = id, CompanyId = companyId };
        item.Update(name, unit, valuation, gstRates, openingQuantityMilli, openingValue, openingGodown, alias, partNumber);
        return item;
    }

    public void Update(
        string name,
        UnitOfMeasure unit,
        ValuationMethod valuation,
        IEnumerable<GstRateEntry>? gstRates,
        long openingQuantityMilli,
        Money openingValue,
        Godown? openingGodown,
        string? alias = null,
        string? partNumber = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (IsDeleted)
        {
            throw new DomainException("stock_item.deleted", "The stock item has been deleted.");
        }

        if (unit.CompanyId != CompanyId || unit.IsDeleted)
        {
            throw new DomainException("stock_item.unit_invalid", "Choose a valid unit.");
        }

        if (!Enum.IsDefined(valuation))
        {
            throw new DomainException("stock_item.valuation_invalid", "Choose a valuation method.");
        }

        if (openingQuantityMilli < 0 || openingValue.IsNegative)
        {
            throw new DomainException("stock_item.opening_negative", "Opening quantity and value cannot be negative.");
        }

        if (openingQuantityMilli == 0 && !openingValue.IsZero)
        {
            throw new DomainException("stock_item.opening_value_without_quantity", "An opening value needs an opening quantity.");
        }

        if (openingQuantityMilli > 0 && (openingGodown is null || openingGodown.CompanyId != CompanyId || openingGodown.IsDeleted))
        {
            throw new DomainException("stock_item.opening_godown_required", "Choose the godown that holds the opening stock.");
        }

        var unitStep = (long)Math.Pow(10, 3 - unit.DecimalPlaces);
        if (openingQuantityMilli % unitStep != 0)
        {
            throw new DomainException("stock.quantity_unit_precision", $"This unit allows {unit.DecimalPlaces} decimal place(s).");
        }

        Name = NameRules.Clean(name, "stock_item_name");
        NormalizedName = NameRules.Normalize(Name);
        Alias = NameRules.CleanOptional(alias, NameRules.MaxLength, "alias");
        PartNumber = NameRules.CleanOptional(partNumber, 50, "part_number");
        UnitId = unit.Id;
        ValuationMethod = valuation;
        GstRates = GstRateSchedule.Normalize(gstRates);
        OpeningQuantityMilli = openingQuantityMilli;
        OpeningValueMinor = openingValue.Minor;
        OpeningGodownId = openingQuantityMilli > 0 ? openingGodown!.Id : null;
    }

    public GstRateEntry? GstRateOn(DateOnly date) => GstRateSchedule.Resolve(GstRates, date);

    /// <summary>The caller must have verified that the item is not used in any voucher.</summary>
    public void MarkDeleted() => IsDeleted = true;
}
