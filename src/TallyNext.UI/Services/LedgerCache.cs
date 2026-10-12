using TallyNext.Application.Accounts;
using TallyNext.Contracts.Accounts;

namespace TallyNext.UI.Services;

/// <summary>
/// Session cache of the company's ledger list for type-to-search pickers. Invalidated after master edits made in
/// this session and after synchronization brings in changes.
/// </summary>
public sealed class LedgerCache(IUseCaseRunner runner)
{
    private Guid? _companyId;
    private IReadOnlyList<LedgerLookupItem>? _items;
    private Task<IReadOnlyList<LedgerLookupItem>>? _loading;

    public async Task<IReadOnlyList<LedgerLookupItem>> GetAsync(Guid companyId)
    {
        if (_companyId == companyId && _items is not null)
        {
            return _items;
        }

        if (_companyId != companyId || _loading is null)
        {
            _companyId = companyId;
            _items = null;
            _loading = runner.RunAsync<ChartOfAccountsService, IReadOnlyList<LedgerLookupItem>>(s => s.LookupLedgersAsync(companyId));
        }

        var loaded = await _loading;
        if (_companyId == companyId)
        {
            _items = loaded;
        }

        return loaded;
    }

    public void Invalidate()
    {
        _items = null;
        _loading = null;
    }

    /// <summary>Prefix matches first, then word-start and substring matches; case-insensitive on name and alias.</summary>
    public static IReadOnlyList<LedgerLookupItem> Search(IReadOnlyList<LedgerLookupItem> items, string? term, Func<LedgerLookupItem, bool>? filter = null, int max = 12)
    {
        var source = filter is null ? items : items.Where(filter);
        if (string.IsNullOrWhiteSpace(term))
        {
            return source.Take(max).ToList();
        }

        var t = term.Trim();
        return source
            .Select(l => (Ledger: l, Rank: Rank(l, t)))
            .Where(x => x.Rank < 4)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Ledger.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Ledger)
            .ToList();
    }

    private static int Rank(LedgerLookupItem ledger, string term)
    {
        if (ledger.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) || (ledger.Alias?.StartsWith(term, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return 0;
        }

        if (ledger.Name.Contains(" " + term, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (ledger.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (ledger.Alias?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return 2;
        }

        return 4;
    }
}
