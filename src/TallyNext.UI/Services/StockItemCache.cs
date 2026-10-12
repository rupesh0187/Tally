using TallyNext.Application.Inventory;
using TallyNext.Contracts.Inventory;

namespace TallyNext.UI.Services;

/// <summary>
/// Session cache of the company's stock items, units and godowns for invoice entry. Invalidated after inventory
/// master edits made in this session and after synchronization brings in changes.
/// </summary>
public sealed class StockItemCache(IUseCaseRunner runner)
{
    private Guid? _companyId;
    private Snapshot? _snapshot;

    public sealed record Snapshot(IReadOnlyList<StockItemLookup> Items, IReadOnlyList<GodownDto> Godowns);

    public async Task<Snapshot> GetAsync(Guid companyId)
    {
        if (_companyId == companyId && _snapshot is not null)
        {
            return _snapshot;
        }

        var items = await runner.RunAsync<InventoryService, IReadOnlyList<StockItemLookup>>(s => s.LookupItemsAsync(companyId));
        var godowns = await runner.RunAsync<InventoryService, IReadOnlyList<GodownDto>>(s => s.ListGodownsAsync(companyId));
        _companyId = companyId;
        _snapshot = new Snapshot(items, godowns);
        return _snapshot;
    }

    public void Invalidate() => _snapshot = null;

    /// <summary>Prefix matches first, then substring matches; case-insensitive on name and alias.</summary>
    public static IReadOnlyList<StockItemLookup> Search(IReadOnlyList<StockItemLookup> items, string? term, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return items.Take(max).ToList();
        }

        var t = term.Trim();
        return items
            .Select(i => (Item: i, Rank: i.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase) || (i.Alias?.StartsWith(t, StringComparison.OrdinalIgnoreCase) ?? false) ? 0
                : i.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (i.Alias?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ? 1 : 2))
            .Where(x => x.Rank < 2)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Item)
            .ToList();
    }
}
