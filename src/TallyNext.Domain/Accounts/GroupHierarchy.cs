using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Domain.Accounts;

/// <summary>
/// In-memory view of a company's group tree: ancestry, primary group, derived ledger traits and cycle checks.
/// Built from the full (small) set of groups of one company.
/// </summary>
public sealed class GroupHierarchy
{
    private const int MaxDepth = 64;
    private readonly Dictionary<Guid, AccountGroup> _groups;
    private readonly Dictionary<Guid, LedgerTraits> _traitsCache = [];

    public GroupHierarchy(IEnumerable<AccountGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _groups = groups.ToDictionary(g => g.Id);
    }

    public IReadOnlyCollection<AccountGroup> All => _groups.Values;

    public AccountGroup? Find(Guid groupId) => _groups.GetValueOrDefault(groupId);

    public AccountGroup Get(Guid groupId) =>
        _groups.TryGetValue(groupId, out var group)
            ? group
            : throw new DomainException("group.not_found", "The group was not found.");

    /// <summary>The group followed by its parent, grandparent, ... up to the primary group.</summary>
    public IReadOnlyList<AccountGroup> AncestorsAndSelf(Guid groupId)
    {
        var chain = new List<AccountGroup>();
        Guid? current = groupId;
        while (current is { } id)
        {
            var group = Get(id);
            chain.Add(group);
            if (chain.Count > MaxDepth)
            {
                throw new DomainException("group.cycle", "The group hierarchy contains a cycle.");
            }

            current = group.ParentGroupId;
        }

        return chain;
    }

    public AccountGroup PrimaryOf(Guid groupId) => AncestorsAndSelf(groupId)[^1];

    public bool IsSameOrDescendantOf(Guid groupId, Guid ancestorId) =>
        AncestorsAndSelf(groupId).Any(g => g.Id == ancestorId);

    public bool HasAncestorWithKey(Guid groupId, string systemKey) =>
        AncestorsAndSelf(groupId).Any(g => g.SystemKey == systemKey);

    public LedgerTraits TraitsOf(Guid groupId)
    {
        if (_traitsCache.TryGetValue(groupId, out var cached))
        {
            return cached;
        }

        var traits = LedgerTraits.None;
        foreach (var group in AncestorsAndSelf(groupId))
        {
            if (group.SystemKey is null)
            {
                continue;
            }

            if (SystemGroupKeys.CashOrBank.Contains(group.SystemKey))
            {
                traits |= LedgerTraits.CashOrBank;
            }
            else if (group.SystemKey == SystemGroupKeys.SalesAccounts)
            {
                traits |= LedgerTraits.SalesAccount;
            }
            else if (group.SystemKey == SystemGroupKeys.PurchaseAccounts)
            {
                traits |= LedgerTraits.PurchaseAccount;
            }
        }

        _traitsCache[groupId] = traits;
        return traits;
    }

    /// <summary>Depth-first order (parents before children, siblings by sort order then name) for display.</summary>
    public IReadOnlyList<(AccountGroup Group, int Depth)> Flatten()
    {
        var children = _groups.Values
            .Where(g => !g.IsDeleted)
            .ToLookup(g => g.ParentGroupId);
        var result = new List<(AccountGroup, int)>(_groups.Count);

        void Visit(Guid? parentId, int depth)
        {
            foreach (var child in children[parentId].OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add((child, depth));
                if (depth < MaxDepth)
                {
                    Visit(child.Id, depth + 1);
                }
            }
        }

        Visit(null, 0);
        return result;
    }
}
