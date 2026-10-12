using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts;
using TallyNext.Contracts.Accounts;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Accounts;

/// <summary>Account groups and ledgers (masters).</summary>
public sealed class ChartOfAccountsService(IAccountingDbContext db, IAccessGuard guard, IAuditTrail audit)
{
    // ---------------------------------------------------------------- groups

    public async Task<IReadOnlyList<AccountGroupDto>> ListGroupsAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);
        var ledgerCounts = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted)
            .GroupBy(l => l.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, cancellationToken);

        return hierarchy.Flatten()
            .Select(x => ToDto(x.Group, hierarchy, x.Depth, ledgerCounts.GetValueOrDefault(x.Group.Id)))
            .ToList();
    }

    public async Task<AccountGroupDto> CreateGroupAsync(Guid companyId, AccountGroupInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: true, cancellationToken);

        AccountGroup group;
        if (input.ParentGroupId is { } parentId)
        {
            var parent = hierarchy.Find(parentId);
            if (parent is null || parent.IsDeleted)
            {
                throw new NotFoundException("parent_group");
            }

            group = AccountGroup.CreateSubGroup(Ids.New(), parent, input.Name, sortOrder: 1000);
        }
        else
        {
            var nature = input.Nature ?? throw new ValidationFailedException("group.nature_required", "Choose the nature of a primary group.");
            group = AccountGroup.CreatePrimary(Ids.New(), companyId, input.Name, nature, input.AffectsGrossProfit, sortOrder: 1000);
        }

        EnsureUniqueGroupName(hierarchy, group.NormalizedName, group.Id);
        db.AccountGroups.Add(group);
        audit.Record(companyId, "group.created", "account-group", group.Id, $"Group '{group.Name}' created", after: SnapshotMapper.ToSnapshot(group));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);

        var updated = new GroupHierarchy(hierarchy.All.Append(group));
        return ToDto(group, updated, updated.AncestorsAndSelf(group.Id).Count - 1, 0);
    }

    public async Task<AccountGroupDto> UpdateGroupAsync(Guid companyId, Guid groupId, AccountGroupInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: true, cancellationToken);
        var group = hierarchy.Find(groupId);
        if (group is null || group.IsDeleted)
        {
            throw new NotFoundException("group");
        }

        EnsureExpectedVersion(input.ExpectedVersion, group.Version, "group");
        var before = SnapshotMapper.ToSnapshot(group);
        group.Rename(input.Name);
        EnsureUniqueGroupName(hierarchy, group.NormalizedName, group.Id);

        if (input.ParentGroupId != group.ParentGroupId)
        {
            if (input.ParentGroupId is not { } newParentId)
            {
                throw new ValidationFailedException("group.cannot_become_primary", "A sub-group cannot be turned into a primary group.");
            }

            var newParent = hierarchy.Find(newParentId);
            if (newParent is null || newParent.IsDeleted)
            {
                throw new NotFoundException("parent_group");
            }

            if (hierarchy.IsSameOrDescendantOf(newParentId, group.Id))
            {
                throw new ValidationFailedException("group.cycle", "A group cannot be moved under itself or one of its sub-groups.");
            }

            // The domain only allows moves under a parent of the same nature, so ledger opening balances
            // (allowed only for balance-sheet natures) remain valid after the move.
            group.MoveUnder(newParent);
        }

        audit.Record(companyId, "group.updated", "account-group", group.Id, $"Group '{group.Name}' updated", before, SnapshotMapper.ToSnapshot(group));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(group, hierarchy, hierarchy.AncestorsAndSelf(group.Id).Count - 1, 0);
    }

    public async Task DeleteGroupAsync(Guid companyId, Guid groupId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var group = await db.AccountGroups.SingleOrDefaultAsync(g => g.CompanyId == companyId && g.Id == groupId && !g.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("group");

        if (await db.AccountGroups.AnyAsync(g => g.CompanyId == companyId && g.ParentGroupId == groupId && !g.IsDeleted, cancellationToken))
        {
            throw new ConflictException("group.has_children", "Delete or move the sub-groups first.");
        }

        if (await db.Ledgers.AnyAsync(l => l.CompanyId == companyId && l.GroupId == groupId && !l.IsDeleted, cancellationToken))
        {
            throw new ConflictException("group.has_ledgers", "Delete or move the ledgers in this group first.");
        }

        var before = SnapshotMapper.ToSnapshot(group);
        group.MarkDeleted();
        audit.Record(companyId, "group.deleted", "account-group", group.Id, $"Group '{group.Name}' deleted", before);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- ledgers

    public async Task<PagedResult<LedgerDto>> ListLedgersAsync(Guid companyId, LedgerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);

        var ledgers = db.Ledgers.AsNoTracking().Where(l => l.CompanyId == companyId && !l.IsDeleted);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = NameRules.Normalize(query.Search.Trim());
            ledgers = ledgers.Where(l => l.NormalizedName.Contains(term) || (l.Alias != null && l.Alias.ToUpper().Contains(term)));
        }

        if (query.GroupId is { } groupId)
        {
            var groupIds = hierarchy.All.Where(g => hierarchy.IsSameOrDescendantOf(g.Id, groupId)).Select(g => g.Id).ToList();
            ledgers = ledgers.Where(l => groupIds.Contains(l.GroupId));
        }

        if (query.CashOrBankOnly)
        {
            var cashGroups = hierarchy.All.Where(g => hierarchy.TraitsOf(g.Id).HasFlag(LedgerTraits.CashOrBank)).Select(g => g.Id).ToList();
            ledgers = ledgers.Where(l => cashGroups.Contains(l.GroupId));
        }

        var total = await ledgers.LongCountAsync(cancellationToken);
        var items = await ledgers
            .OrderBy(l => l.NormalizedName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<LedgerDto>(items.Select(l => ToDto(l, hierarchy)).ToList(), page, pageSize, total);
    }

    /// <summary>
    /// Compact list of every active ledger, ordered by name, for type-to-search pickers. Clients cache it and
    /// refresh it after local saves and after sync applies master changes.
    /// </summary>
    public async Task<IReadOnlyList<LedgerLookupItem>> LookupLedgersAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);
        var rows = await db.Ledgers.AsNoTracking()
            .Where(l => l.CompanyId == companyId && !l.IsDeleted)
            .OrderBy(l => l.NormalizedName)
            .Select(l => new { l.Id, l.Name, l.Alias, l.GroupId })
            .ToListAsync(cancellationToken);

        return rows.Select(l =>
        {
            var group = hierarchy.Get(l.GroupId);
            return new LedgerLookupItem(l.Id, l.Name, l.Alias, l.GroupId, group.Name, group.Nature, hierarchy.TraitsOf(l.GroupId));
        }).ToList();
    }

    public async Task<LedgerDto> GetLedgerAsync(Guid companyId, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterView, cancellationToken);
        var ledger = await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == ledgerId && !l.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("ledger");
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);
        return ToDto(ledger, hierarchy);
    }

    public async Task<LedgerDto> CreateLedgerAsync(Guid companyId, LedgerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        var opening = Amounts.Parse(input.OpeningBalance, nameof(input.OpeningBalance));
        var id = input.Id ?? Ids.New();

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);

        var existing = await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (existing is not null)
        {
            if (existing.CompanyId != companyId)
            {
                throw new ConflictException("ledger.id_conflict", "A ledger with this identifier already exists.");
            }

            return ToDto(existing, hierarchy);
        }

        var group = hierarchy.Find(input.GroupId);
        if (group is null || group.IsDeleted)
        {
            throw new NotFoundException("group");
        }

        var ledger = Ledger.Create(id, group, input.Name, opening, input.OpeningSide, ToDetails(input));
        await EnsureUniqueLedgerNameAsync(companyId, ledger.NormalizedName, ledger.Id, cancellationToken);
        db.Ledgers.Add(ledger);
        audit.Record(companyId, "ledger.created", "ledger", ledger.Id, $"Ledger '{ledger.Name}' created", after: SnapshotMapper.ToSnapshot(ledger));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(ledger, hierarchy);
    }

    public async Task<LedgerDto> UpdateLedgerAsync(Guid companyId, Guid ledgerId, LedgerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        var opening = Amounts.Parse(input.OpeningBalance, nameof(input.OpeningBalance));

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var hierarchy = await LoadHierarchyAsync(companyId, tracked: false, cancellationToken);
        var ledger = await db.Ledgers.SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == ledgerId && !l.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("ledger");
        EnsureExpectedVersion(input.ExpectedVersion, ledger.Version, "ledger");

        var group = hierarchy.Find(input.GroupId);
        if (group is null || group.IsDeleted)
        {
            throw new NotFoundException("group");
        }

        var before = SnapshotMapper.ToSnapshot(ledger);
        ledger.Update(group, input.Name, opening, input.OpeningSide, ToDetails(input));
        await EnsureUniqueLedgerNameAsync(companyId, ledger.NormalizedName, ledger.Id, cancellationToken);
        audit.Record(companyId, "ledger.updated", "ledger", ledger.Id, $"Ledger '{ledger.Name}' updated", before, SnapshotMapper.ToSnapshot(ledger));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return ToDto(ledger, hierarchy);
    }

    /// <summary>Deletes (tombstones) a ledger that has never been used in any voucher, including drafts.</summary>
    public async Task DeleteLedgerAsync(Guid companyId, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MasterEdit, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var ledger = await db.Ledgers.SingleOrDefaultAsync(l => l.CompanyId == companyId && l.Id == ledgerId && !l.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("ledger");

        if (await db.VoucherLines.AnyAsync(l => l.CompanyId == companyId && l.LedgerId == ledgerId, cancellationToken))
        {
            throw new ConflictException("ledger.in_use", "This ledger is used in vouchers and cannot be deleted.");
        }

        var before = SnapshotMapper.ToSnapshot(ledger);
        ledger.MarkDeleted();
        audit.Record(companyId, "ledger.deleted", "ledger", ledger.Id, $"Ledger '{ledger.Name}' deleted", before);
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    internal async Task<GroupHierarchy> LoadHierarchyAsync(Guid companyId, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.AccountGroups.Where(g => g.CompanyId == companyId);
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return new GroupHierarchy(await query.ToListAsync(cancellationToken));
    }

    private static void EnsureUniqueGroupName(GroupHierarchy hierarchy, string normalizedName, Guid id)
    {
        if (hierarchy.All.Any(g => !g.IsDeleted && g.Id != id && g.NormalizedName == normalizedName))
        {
            throw new ConflictException("group.duplicate_name", "A group with this name already exists.");
        }
    }

    private async Task EnsureUniqueLedgerNameAsync(Guid companyId, string normalizedName, Guid id, CancellationToken cancellationToken)
    {
        if (await db.Ledgers.AnyAsync(l => l.CompanyId == companyId && !l.IsDeleted && l.Id != id && l.NormalizedName == normalizedName, cancellationToken))
        {
            throw new ConflictException("ledger.duplicate_name", "A ledger with this name already exists.");
        }
    }

    private static void EnsureExpectedVersion(long? expected, long actual, string entity)
    {
        if (expected is { } e && e != actual)
        {
            throw new ConflictException($"{entity}.version_conflict", $"The {entity} was changed by someone else. Reload and try again.", actual);
        }
    }

    private static LedgerDetails ToDetails(LedgerInput input) => new(
        input.Alias, input.IsBillWise, input.GstRegistrationType, input.Gstin, input.StateCode, input.Pan,
        input.AddressLine1, input.AddressLine2, input.City, input.Pincode, input.Email, input.Phone, input.CreditPeriodDays);

    internal static LedgerDto ToDto(Ledger l, GroupHierarchy hierarchy)
    {
        var group = hierarchy.Get(l.GroupId);
        return new LedgerDto(
            l.Id, l.Name, l.Alias, l.GroupId, group.Name, group.Nature, hierarchy.TraitsOf(l.GroupId),
            Amounts.ToDecimal(l.OpeningBalanceMinor), l.OpeningSide, l.IsBillWise, l.IsSystem, l.GstRegistrationType,
            l.Gstin, l.StateCode, l.Pan, l.AddressLine1, l.AddressLine2, l.City, l.Pincode, l.Email, l.Phone, l.CreditPeriodDays, l.Version);
    }

    private static AccountGroupDto ToDto(AccountGroup g, GroupHierarchy hierarchy, int depth, int ledgerCount)
    {
        var parentName = g.ParentGroupId is { } p ? hierarchy.Find(p)?.Name : null;
        return new AccountGroupDto(g.Id, g.Name, g.ParentGroupId, parentName, g.Nature, g.AffectsGrossProfit, g.IsSystem, depth, ledgerCount, g.Version);
    }
}
