using TallyNext.Contracts.Accounts;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;

namespace TallyNext.Contracts.Inventory;

public sealed record UnitDto(Guid Id, string Symbol, string Name, int DecimalPlaces, long Version);

public sealed class UnitInput
{
    public string Symbol { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int DecimalPlaces { get; set; }

    public Guid? Id { get; set; }
}

public sealed record GodownDto(Guid Id, string Name, bool IsSystem, long Version);

public sealed class GodownInput
{
    public string Name { get; set; } = string.Empty;

    public Guid? Id { get; set; }
}

public sealed record StockItemDto(
    Guid Id,
    string Name,
    string? Alias,
    string? PartNumber,
    Guid UnitId,
    string UnitSymbol,
    int UnitDecimalPlaces,
    ValuationMethod ValuationMethod,
    IReadOnlyList<GstRateEntry> GstRates,
    decimal OpeningQuantity,
    decimal OpeningValue,
    Guid? OpeningGodownId,
    long Version);

public sealed class StockItemInput
{
    public string Name { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public string? PartNumber { get; set; }

    public Guid UnitId { get; set; }

    public ValuationMethod ValuationMethod { get; set; } = ValuationMethod.WeightedAverage;

    public List<GstRateInput> GstRates { get; set; } = [];

    public decimal OpeningQuantity { get; set; }

    public decimal OpeningValue { get; set; }

    public Guid? OpeningGodownId { get; set; }

    public Guid? Id { get; set; }

    public long? ExpectedVersion { get; set; }
}

/// <summary>Compact item list for invoice entry (cached per session).</summary>
public sealed record StockItemLookup(
    Guid Id,
    string Name,
    string? Alias,
    string UnitSymbol,
    int UnitDecimalPlaces,
    IReadOnlyList<GstRateEntry> GstRates);
