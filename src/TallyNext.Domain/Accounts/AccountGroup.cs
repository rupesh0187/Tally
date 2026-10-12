using TallyNext.Domain.Common;

namespace TallyNext.Domain.Accounts;

/// <summary>
/// Hierarchical classification of ledgers. Primary groups carry the nature; sub-groups inherit it
/// (and the gross-profit flag) from their parent, so a sub-group can never contradict its ancestors.
/// </summary>
public sealed class AccountGroup : CompanyScopedEntity
{
    private AccountGroup()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public Guid? ParentGroupId { get; private set; }

    public AccountNature Nature { get; private set; }

    /// <summary>True for direct income/expense groups (trading account items above gross profit).</summary>
    public bool AffectsGrossProfit { get; private set; }

    /// <summary>Non-null for predefined groups; see <see cref="SystemGroupKeys"/>.</summary>
    public string? SystemKey { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsPrimary => ParentGroupId is null;

    public bool IsSystem => SystemKey is not null;

    public static AccountGroup CreatePrimary(
        Guid id, Guid companyId, string name, AccountNature nature, bool affectsGrossProfit, string? systemKey = null, int sortOrder = 0)
    {
        if (!Enum.IsDefined(nature))
        {
            throw new DomainException("group.nature_invalid", "The group nature is not valid.");
        }

        if (affectsGrossProfit && !nature.IsProfitAndLoss())
        {
            throw new DomainException("group.gross_profit_invalid", "Only income or expense groups can affect gross profit.");
        }

        var group = new AccountGroup
        {
            Id = id,
            CompanyId = companyId,
            Nature = nature,
            AffectsGrossProfit = affectsGrossProfit,
            SystemKey = systemKey,
            SortOrder = sortOrder,
        };
        group.SetName(name);
        return group;
    }

    public static AccountGroup CreateSubGroup(Guid id, AccountGroup parent, string name, string? systemKey = null, int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.IsDeleted)
        {
            throw new DomainException("group.parent_deleted", "The parent group has been deleted.");
        }

        var group = new AccountGroup
        {
            Id = id,
            CompanyId = parent.CompanyId,
            ParentGroupId = parent.Id,
            Nature = parent.Nature,
            AffectsGrossProfit = parent.AffectsGrossProfit,
            SystemKey = systemKey,
            SortOrder = sortOrder,
        };
        group.SetName(name);
        return group;
    }

    public void Rename(string name)
    {
        EnsureNotDeleted();
        SetName(name);
    }

    /// <summary>
    /// Moves a user-defined sub-group under another parent with the same nature. Cycle detection needs the
    /// whole tree and is performed by the application layer before calling this method.
    /// </summary>
    public void MoveUnder(AccountGroup newParent)
    {
        ArgumentNullException.ThrowIfNull(newParent);
        EnsureNotDeleted();
        if (IsSystem)
        {
            throw new DomainException("group.system_readonly", "Predefined groups cannot be moved.");
        }

        if (IsPrimary)
        {
            throw new DomainException("group.primary_immovable", "A primary group cannot be moved under another group.");
        }

        if (newParent.CompanyId != CompanyId || newParent.Id == Id || newParent.IsDeleted)
        {
            throw new DomainException("group.parent_invalid", "The parent group is not valid.");
        }

        if (newParent.Nature != Nature)
        {
            throw new DomainException("group.nature_change", "A group can only be moved under a parent with the same nature.");
        }

        ParentGroupId = newParent.Id;
        AffectsGrossProfit = newParent.AffectsGrossProfit;
    }

    public void MarkDeleted()
    {
        if (IsSystem)
        {
            throw new DomainException("group.system_readonly", "Predefined groups cannot be deleted.");
        }

        IsDeleted = true;
    }

    private void SetName(string name)
    {
        Name = NameRules.Clean(name, "group_name");
        NormalizedName = NameRules.Normalize(Name);
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("group.deleted", "The group has been deleted.");
        }
    }
}
