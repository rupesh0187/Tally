using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Sync;

/// <summary>
/// Applies one uploaded change inside the company write lock, using domain methods and the same validation as
/// online saves. Throws <see cref="SyncConflictException"/>, <see cref="SyncDeferredException"/>,
/// <see cref="SyncRejectedException"/>, <see cref="DomainException"/> or application exceptions for every outcome
/// other than acceptance; the caller maps those to protocol outcomes.
/// </summary>
internal sealed class SyncChangeApplier(
    ServerDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    VoucherPostingRules rules,
    TimeProvider clock)
{
    /// <summary>Applies the change. Returns the entity's new server version.</summary>
    public async Task<ISyncEntity> ApplyAsync(Guid userId, Device device, SyncChangeUpload change, CancellationToken cancellationToken)
    {
        if (change.EntityType != SyncEntityTypes.Company
            && !await db.Companies.AnyAsync(c => c.Id == change.CompanyId, cancellationToken))
        {
            // The company itself is still on its way (or its upload is waiting for a fix): retry later rather than
            // recording a permanent "forbidden" for every row of a company created offline.
            throw new SyncDeferredException("sync.dependency_missing", "The company has not been uploaded yet.");
        }

        return change.EntityType switch
        {
            SyncEntityTypes.Company => await ApplyCompanyAsync(userId, change, cancellationToken),
            SyncEntityTypes.AccountGroup => await ApplyGroupAsync(change, cancellationToken),
            SyncEntityTypes.Ledger => await ApplyLedgerAsync(change, cancellationToken),
            SyncEntityTypes.VoucherType => await ApplyVoucherTypeAsync(change, cancellationToken),
            SyncEntityTypes.Voucher => await ApplyVoucherAsync(userId, device, change, cancellationToken),
            SyncEntityTypes.Unit => await ApplyUnitAsync(change, cancellationToken),
            SyncEntityTypes.Godown => await ApplyGodownAsync(change, cancellationToken),
            SyncEntityTypes.StockItem => await ApplyStockItemAsync(change, cancellationToken),
            _ => throw new SyncRejectedException("sync.entity_type_not_uploadable", $"'{change.EntityType}' cannot be uploaded."),
        };
    }

    // ------------------------------------------------------------------ company

    private async Task<Company> ApplyCompanyAsync(Guid userId, SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<CompanySnapshot>(change);
        if (s.Id != change.EntityId || s.Id != change.CompanyId)
        {
            throw new SyncRejectedException("sync.id_mismatch", "The company id does not match the change envelope.");
        }

        var existing = await db.Companies.SingleOrDefaultAsync(c => c.Id == s.Id, ct);
        if (existing is null)
        {
            if (change.BaseVersion != 0)
            {
                throw new SyncConflictException("sync.row_missing", 0, null);
            }

            if (s.CreatedByUserId != userId)
            {
                throw new SyncRejectedException("company.creator_mismatch", "A company can only be uploaded by the user who created it.");
            }

            var company = Company.Create(s.Id, ProfileOf(s), s.CurrencyCode, s.FinancialYearStartMonth, s.BooksBeginningDate, userId);
            company.SetLockedThrough(s.LockedThroughDate);
            company.SetGstRegistration(s.GstRegistration);
            db.Companies.Add(company);
            if (!await db.CompanyMemberships.AnyAsync(m => m.CompanyId == s.Id && m.UserId == userId, ct))
            {
                var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
                db.CompanyMemberships.Add(CompanyMembership.Create(s.Id, userId, CompanyRole.Owner, user.Email ?? string.Empty, user.DisplayName));
            }

            audit.Record(s.Id, "company.created", "company", s.Id, $"Company '{company.Name}' created on a device", after: SnapshotMapper.ToSnapshot(company), changeId: change.ChangeId);
            return company;
        }

        EnsureVersion(existing, change);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            throw new SyncRejectedException("company.delete_not_supported", "Companies cannot be deleted by synchronization.");
        }

        if (existing.CurrencyCode != s.CurrencyCode || existing.FinancialYearStartMonth != s.FinancialYearStartMonth || existing.BooksBeginningDate != s.BooksBeginningDate)
        {
            throw new SyncRejectedException("company.immutable_field", "Currency, financial year and books beginning date cannot be changed.");
        }

        var before = SnapshotMapper.ToSnapshot(existing);
        if (existing.LockedThroughDate != s.LockedThroughDate)
        {
            await guard.RequireAsync(s.Id, Permission.PeriodLock, ct);
            existing.SetLockedThrough(s.LockedThroughDate);
        }

        // Order matters: a GSTIN can only be removed once the company is no longer GST registered, and registering
        // needs the GSTIN first.
        var registrationChanged = existing.GstRegistration != s.GstRegistration;
        if (registrationChanged)
        {
            await guard.RequireAsync(s.Id, Permission.CompanyManage, ct);
        }

        if (registrationChanged && s.GstRegistration == GstRegistrationType.NotApplicable)
        {
            existing.SetGstRegistration(s.GstRegistration);
        }

        if (ProfileOf(s) != existing.GetProfile())
        {
            await guard.RequireAsync(s.Id, Permission.CompanyManage, ct);
            existing.UpdateProfile(ProfileOf(s));
        }

        if (registrationChanged && s.GstRegistration != GstRegistrationType.NotApplicable)
        {
            existing.SetGstRegistration(s.GstRegistration);
        }

        audit.Record(s.Id, "company.updated", "company", s.Id, $"Company '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    // ------------------------------------------------------------------ groups

    private async Task<AccountGroup> ApplyGroupAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<AccountGroupSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var groups = await db.AccountGroups.Where(g => g.CompanyId == s.CompanyId).ToListAsync(ct);
        var hierarchy = new GroupHierarchy(groups);
        var existing = groups.SingleOrDefault(g => g.Id == s.Id);

        if (existing is null)
        {
            EnsureNew(change);
            if (s.SystemKey is not null && groups.Any(g => g.SystemKey == s.SystemKey && !g.IsDeleted))
            {
                throw new SyncRejectedException("group.system_key_exists", "This predefined group already exists in the company.");
            }

            AccountGroup group;
            if (s.ParentGroupId is { } parentId)
            {
                var parent = hierarchy.Find(parentId);
                if (parent is null)
                {
                    throw new SyncDeferredException("sync.dependency_missing", "The parent group has not been uploaded yet.");
                }

                group = AccountGroup.CreateSubGroup(s.Id, parent, s.Name, s.SystemKey, s.SortOrder);
                if (group.Nature != s.Nature)
                {
                    throw new SyncRejectedException("group.nature_mismatch", "A sub-group must have the nature of its parent.");
                }
            }
            else
            {
                group = AccountGroup.CreatePrimary(s.Id, s.CompanyId, s.Name, s.Nature, s.AffectsGrossProfit, s.SystemKey, s.SortOrder);
            }

            EnsureUniqueName(groups, group.NormalizedName, group.Id, "group");
            db.AccountGroups.Add(group);
            audit.Record(s.CompanyId, "group.created", "account-group", group.Id, $"Group '{group.Name}' created on a device", after: SnapshotMapper.ToSnapshot(group), changeId: change.ChangeId);
            return group;
        }

        EnsureVersion(existing, change);
        var before = SnapshotMapper.ToSnapshot(existing);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            if (groups.Any(g => g.ParentGroupId == existing.Id && !g.IsDeleted)
                || await db.Ledgers.AnyAsync(l => l.CompanyId == s.CompanyId && l.GroupId == existing.Id && !l.IsDeleted, ct))
            {
                throw new SyncRejectedException("group.in_use", "The group still has sub-groups or ledgers.");
            }

            existing.MarkDeleted();
            audit.Record(s.CompanyId, "group.deleted", "account-group", existing.Id, $"Group '{existing.Name}' deleted on a device", before, changeId: change.ChangeId);
            return existing;
        }

        existing.Rename(s.Name);
        EnsureUniqueName(groups, existing.NormalizedName, existing.Id, "group");
        if (s.ParentGroupId != existing.ParentGroupId)
        {
            var newParent = s.ParentGroupId is { } p ? hierarchy.Find(p) : null;
            if (newParent is null)
            {
                throw new SyncDeferredException("sync.dependency_missing", "The new parent group has not been uploaded yet.");
            }

            if (hierarchy.IsSameOrDescendantOf(newParent.Id, existing.Id))
            {
                throw new SyncRejectedException("group.cycle", "A group cannot be moved under itself.");
            }

            existing.MoveUnder(newParent);
        }

        audit.Record(s.CompanyId, "group.updated", "account-group", existing.Id, $"Group '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    // ------------------------------------------------------------------ ledgers

    private async Task<Ledger> ApplyLedgerAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<LedgerSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var group = await db.AccountGroups.SingleOrDefaultAsync(g => g.CompanyId == s.CompanyId && g.Id == s.GroupId, ct)
            ?? throw new SyncDeferredException("sync.dependency_missing", "The ledger's group has not been uploaded yet.");
        var existing = await db.Ledgers.SingleOrDefaultAsync(l => l.Id == s.Id, ct);
        var details = new LedgerDetails(
            s.Alias, s.IsBillWise, s.GstRegistrationType, s.Gstin, s.StateCode, s.Pan, s.AddressLine1, s.AddressLine2,
            s.City, s.Pincode, s.Email, s.Phone, s.CreditPeriodDays, s.GstTaxType, s.GstRates);
        if (s.GstTaxType != GstTaxType.None)
        {
            var hierarchy = new GroupHierarchy(await db.AccountGroups.AsNoTracking().Where(g => g.CompanyId == s.CompanyId).ToListAsync(ct));
            if (!hierarchy.HasAncestorWithKey(group.Id, SystemGroupKeys.DutiesAndTaxes))
            {
                throw new SyncRejectedException("ledger.gst_tax_type_group", "GST tax ledgers must be under Duties & Taxes.");
            }
        }

        if (existing is null)
        {
            EnsureNew(change);
            if (s.SystemKey is not null && await db.Ledgers.AnyAsync(l => l.CompanyId == s.CompanyId && l.SystemKey == s.SystemKey && !l.IsDeleted, ct))
            {
                throw new SyncRejectedException("ledger.system_key_exists", "This predefined ledger already exists in the company.");
            }

            var ledger = Ledger.Create(s.Id, group, s.Name, Money.FromMinor(s.OpeningBalanceMinor), s.OpeningSide, details, s.SystemKey);
            await EnsureUniqueLedgerNameAsync(s.CompanyId, ledger.NormalizedName, ledger.Id, ct);
            db.Ledgers.Add(ledger);
            audit.Record(s.CompanyId, "ledger.created", "ledger", ledger.Id, $"Ledger '{ledger.Name}' created on a device", after: SnapshotMapper.ToSnapshot(ledger), changeId: change.ChangeId);
            return ledger;
        }

        if (existing.CompanyId != s.CompanyId)
        {
            throw new SyncRejectedException("sync.id_mismatch", "The ledger belongs to another company.");
        }

        EnsureVersion(existing, change);
        var before = SnapshotMapper.ToSnapshot(existing);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            if (await db.VoucherLines.AnyAsync(l => l.CompanyId == s.CompanyId && l.LedgerId == existing.Id, ct)
                || await db.VoucherItemLines.AnyAsync(l => l.CompanyId == s.CompanyId && l.LedgerId == existing.Id, ct)
                || await db.Vouchers.AnyAsync(v => v.CompanyId == s.CompanyId && v.PartyLedgerId == existing.Id, ct))
            {
                throw new SyncRejectedException("ledger.in_use", "This ledger is used in vouchers and cannot be deleted.");
            }

            existing.MarkDeleted();
            audit.Record(s.CompanyId, "ledger.deleted", "ledger", existing.Id, $"Ledger '{existing.Name}' deleted on a device", before, changeId: change.ChangeId);
            return existing;
        }

        existing.Update(group, s.Name, Money.FromMinor(s.OpeningBalanceMinor), s.OpeningSide, details);
        await EnsureUniqueLedgerNameAsync(s.CompanyId, existing.NormalizedName, existing.Id, ct);
        audit.Record(s.CompanyId, "ledger.updated", "ledger", existing.Id, $"Ledger '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    // ------------------------------------------------------------------ voucher types

    private async Task<VoucherType> ApplyVoucherTypeAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<VoucherTypeSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var types = await db.VoucherTypes.Where(t => t.CompanyId == s.CompanyId).ToListAsync(ct);
        var existing = types.SingleOrDefault(t => t.Id == s.Id);
        if (existing is null)
        {
            EnsureNew(change);
            VoucherType type;
            if (s.IsSystem)
            {
                if (types.Any(t => t.IsSystem && t.BaseType == s.BaseType && !t.IsDeleted))
                {
                    throw new SyncRejectedException("voucher_type.system_exists", "This predefined voucher type already exists in the company.");
                }

                type = VoucherType.CreateSystem(s.Id, s.CompanyId, s.BaseType, s.Name, s.Abbreviation);
            }
            else
            {
                type = VoucherType.Create(s.Id, s.CompanyId, s.BaseType, s.Name, s.Abbreviation);
            }

            if (types.Any(t => !t.IsDeleted && t.NormalizedName == type.NormalizedName))
            {
                throw new SyncRejectedException("voucher_type.duplicate_name", "A voucher type with this name already exists.");
            }

            db.VoucherTypes.Add(type);
            audit.Record(s.CompanyId, "voucher_type.created", "voucher-type", type.Id, $"Voucher type '{type.Name}' created on a device", after: SnapshotMapper.ToSnapshot(type), changeId: change.ChangeId);
            return type;
        }

        EnsureVersion(existing, change);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            throw new SyncRejectedException("voucher_type.delete_not_supported", "Voucher types are deactivated, not deleted.");
        }

        var before = SnapshotMapper.ToSnapshot(existing);
        existing.Update(s.Name, s.Abbreviation, s.IsActive);
        audit.Record(s.CompanyId, "voucher_type.updated", "voucher-type", existing.Id, $"Voucher type '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    // ------------------------------------------------------------------ inventory masters

    private async Task<UnitOfMeasure> ApplyUnitAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<UnitSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var existing = await db.Units.SingleOrDefaultAsync(u => u.Id == s.Id, ct);
        if (existing is null)
        {
            EnsureNew(change);
            var unit = UnitOfMeasure.Create(s.Id, s.CompanyId, s.Symbol, s.Name, s.DecimalPlaces);
            await EnsureUniqueUnitAsync(unit, ct);
            db.Units.Add(unit);
            audit.Record(s.CompanyId, "unit.created", "unit", unit.Id, $"Unit '{unit.Symbol}' created on a device", after: SnapshotMapper.ToSnapshot(unit), changeId: change.ChangeId);
            return unit;
        }

        EnsureSameCompany(existing.CompanyId, s.CompanyId);
        EnsureVersion(existing, change);
        var before = SnapshotMapper.ToSnapshot(existing);
        var inUse = await db.StockItems.AnyAsync(i => i.CompanyId == s.CompanyId && i.UnitId == existing.Id && !i.IsDeleted, ct);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            if (inUse)
            {
                throw new SyncRejectedException("unit.in_use", "Stock items still use this unit.");
            }

            existing.MarkDeleted();
            audit.Record(s.CompanyId, "unit.deleted", "unit", existing.Id, $"Unit '{existing.Symbol}' deleted on a device", before, changeId: change.ChangeId);
            return existing;
        }

        if (s.DecimalPlaces < existing.DecimalPlaces && inUse)
        {
            throw new SyncRejectedException("unit.decimals_in_use", "Decimal places cannot be reduced for a unit already used by stock items.");
        }

        existing.Update(s.Symbol, s.Name, s.DecimalPlaces);
        await EnsureUniqueUnitAsync(existing, ct);
        audit.Record(s.CompanyId, "unit.updated", "unit", existing.Id, $"Unit '{existing.Symbol}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    private async Task<Godown> ApplyGodownAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<GodownSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var existing = await db.Godowns.SingleOrDefaultAsync(g => g.Id == s.Id, ct);
        if (existing is null)
        {
            EnsureNew(change);
            if (s.IsSystem && await db.Godowns.AnyAsync(g => g.CompanyId == s.CompanyId && g.IsSystem && !g.IsDeleted, ct))
            {
                throw new SyncRejectedException("godown.system_exists", "The company already has its main location.");
            }

            var godown = Godown.Create(s.Id, s.CompanyId, s.Name, s.IsSystem);
            await EnsureUniqueGodownAsync(godown, ct);
            db.Godowns.Add(godown);
            audit.Record(s.CompanyId, "godown.created", "godown", godown.Id, $"Godown '{godown.Name}' created on a device", after: SnapshotMapper.ToSnapshot(godown), changeId: change.ChangeId);
            return godown;
        }

        EnsureSameCompany(existing.CompanyId, s.CompanyId);
        EnsureVersion(existing, change);
        var before = SnapshotMapper.ToSnapshot(existing);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            if (await db.StockItems.AnyAsync(i => i.CompanyId == s.CompanyId && i.OpeningGodownId == existing.Id && !i.IsDeleted, ct)
                || await db.VoucherItemLines.AnyAsync(l => l.CompanyId == s.CompanyId && l.GodownId == existing.Id, ct))
            {
                throw new SyncRejectedException("godown.in_use", "The godown holds stock or is used in vouchers.");
            }

            existing.MarkDeleted();
            audit.Record(s.CompanyId, "godown.deleted", "godown", existing.Id, $"Godown '{existing.Name}' deleted on a device", before, changeId: change.ChangeId);
            return existing;
        }

        existing.Rename(s.Name);
        await EnsureUniqueGodownAsync(existing, ct);
        audit.Record(s.CompanyId, "godown.updated", "godown", existing.Id, $"Godown '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    private async Task<StockItem> ApplyStockItemAsync(SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<StockItemSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.MasterEdit, ct);
        var existing = await db.StockItems.SingleOrDefaultAsync(i => i.Id == s.Id, ct);
        if (existing is not null)
        {
            EnsureSameCompany(existing.CompanyId, s.CompanyId);
            EnsureVersion(existing, change);
            if (change.Operation == SyncOperation.Delete || s.IsDeleted)
            {
                if (await db.VoucherItemLines.AnyAsync(l => l.CompanyId == s.CompanyId && l.StockItemId == existing.Id, ct))
                {
                    throw new SyncRejectedException("stock_item.in_use", "This item is used in vouchers and cannot be deleted.");
                }

                var deletedBefore = SnapshotMapper.ToSnapshot(existing);
                existing.MarkDeleted();
                audit.Record(s.CompanyId, "stock_item.deleted", "stock-item", existing.Id, $"Stock item '{existing.Name}' deleted on a device", deletedBefore, changeId: change.ChangeId);
                return existing;
            }
        }

        var unit = await db.Units.SingleOrDefaultAsync(u => u.CompanyId == s.CompanyId && u.Id == s.UnitId, ct)
            ?? throw new SyncDeferredException("sync.dependency_missing", "The item's unit has not been uploaded yet.");
        Godown? godown = null;
        if (s.OpeningGodownId is { } godownId)
        {
            godown = await db.Godowns.SingleOrDefaultAsync(g => g.CompanyId == s.CompanyId && g.Id == godownId, ct)
                ?? throw new SyncDeferredException("sync.dependency_missing", "The item's opening godown has not been uploaded yet.");
        }

        if (existing is null)
        {
            EnsureNew(change);
            var item = StockItem.Create(
                s.Id, s.CompanyId, s.Name, unit, s.ValuationMethod, s.GstRates, s.OpeningQuantityMilli, Money.FromMinor(s.OpeningValueMinor), godown, s.Alias, s.PartNumber);
            await EnsureUniqueItemAsync(item, ct);
            db.StockItems.Add(item);
            audit.Record(s.CompanyId, "stock_item.created", "stock-item", item.Id, $"Stock item '{item.Name}' created on a device", after: SnapshotMapper.ToSnapshot(item), changeId: change.ChangeId);
            return item;
        }

        var before = SnapshotMapper.ToSnapshot(existing);
        existing.Update(s.Name, unit, s.ValuationMethod, s.GstRates, s.OpeningQuantityMilli, Money.FromMinor(s.OpeningValueMinor), godown, s.Alias, s.PartNumber);
        await EnsureUniqueItemAsync(existing, ct);
        audit.Record(s.CompanyId, "stock_item.updated", "stock-item", existing.Id, $"Stock item '{existing.Name}' updated on a device", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    private async Task EnsureUniqueUnitAsync(UnitOfMeasure unit, CancellationToken ct)
    {
        if (await db.Units.AnyAsync(u => u.CompanyId == unit.CompanyId && !u.IsDeleted && u.Id != unit.Id && u.NormalizedSymbol == unit.NormalizedSymbol, ct))
        {
            throw new SyncRejectedException("unit.duplicate_symbol", "A unit with this symbol already exists.");
        }
    }

    private async Task EnsureUniqueGodownAsync(Godown godown, CancellationToken ct)
    {
        if (await db.Godowns.AnyAsync(g => g.CompanyId == godown.CompanyId && !g.IsDeleted && g.Id != godown.Id && g.NormalizedName == godown.NormalizedName, ct))
        {
            throw new SyncRejectedException("godown.duplicate_name", "A godown with this name already exists.");
        }
    }

    private async Task EnsureUniqueItemAsync(StockItem item, CancellationToken ct)
    {
        if (await db.StockItems.AnyAsync(i => i.CompanyId == item.CompanyId && !i.IsDeleted && i.Id != item.Id && i.NormalizedName == item.NormalizedName, ct))
        {
            throw new SyncRejectedException("stock_item.duplicate_name", "A stock item with this name already exists.");
        }
    }

    private static void EnsureSameCompany(Guid existingCompanyId, Guid snapshotCompanyId)
    {
        if (existingCompanyId != snapshotCompanyId)
        {
            throw new SyncRejectedException("sync.id_mismatch", "The record belongs to another company.");
        }
    }

    // ------------------------------------------------------------------ vouchers

    private async Task<Voucher> ApplyVoucherAsync(Guid userId, Device device, SyncChangeUpload change, CancellationToken ct)
    {
        var s = Read<VoucherSnapshot>(change);
        EnsureEnvelope(change, s.Id, s.CompanyId);
        await guard.RequireAsync(s.CompanyId, Permission.VoucherCreate, ct);
        if (change.Operation == SyncOperation.Delete || s.IsDeleted)
        {
            throw new SyncRejectedException("voucher.delete_not_supported", "Vouchers are never deleted; drafts are cancelled and posted vouchers reversed.");
        }

        var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == s.CompanyId, ct);
        var type = await db.VoucherTypes.SingleOrDefaultAsync(t => t.CompanyId == s.CompanyId && t.Id == s.VoucherTypeId, ct)
            ?? throw new SyncDeferredException("sync.dependency_missing", "The voucher type has not been uploaded yet.");
        await EnsureLedgersPresentAsync(s, ct);

        var lines = s.Lines.OrderBy(l => l.LineNo)
            .Select(l => new VoucherLineSpec(l.LedgerId, l.Side, Money.FromMinor(l.AmountMinor), l.Narration, l.Id))
            .ToList();
        var header = new VoucherHeader(s.Narration, s.Reference, s.ReferenceDate);

        var itemLines = ItemLinesOf(s);
        var existing = await db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines).SingleOrDefaultAsync(v => v.Id == s.Id, ct);
        if (existing is null)
        {
            EnsureNew(change);
            if (s.CreatedByUserId != userId)
            {
                throw new SyncRejectedException("voucher.creator_mismatch", "A voucher can only be uploaded by the user who created it.");
            }

            Voucher voucher;
            if (s.ReversalOfVoucherId is { } originalId)
            {
                await guard.RequireAsync(s.CompanyId, Permission.VoucherReverse, ct);
                var original = await db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines).SingleOrDefaultAsync(v => v.CompanyId == s.CompanyId && v.Id == originalId, ct)
                    ?? throw new SyncDeferredException("sync.dependency_missing", "The reversed voucher has not been uploaded yet.");
                if (await db.Vouchers.AnyAsync(v => v.ReversalOfVoucherId == originalId, ct))
                {
                    throw new SyncRejectedException("voucher.already_reversed", "This voucher has already been reversed (possibly from another device).");
                }

                voucher = Voucher.CreateReversal(s.Id, company, original, s.Date, s.Narration, userId, device.Id);
                var expected = voucher.Lines.Select(l => (l.LedgerId, l.Side, l.AmountMinor)).Order().ToList();
                var actual = s.Lines.Select(l => (l.LedgerId, l.Side, l.AmountMinor)).Order().ToList();
                if (!expected.SequenceEqual(actual) || !ItemLinesMatch(voucher, s))
                {
                    throw new SyncRejectedException("voucher.reversal_mismatch", "The reversal does not mirror the original voucher.");
                }
            }
            else
            {
                voucher = Voucher.CreateDraft(s.Id, company, type, s.Date, header, lines, userId, device.Id, s.IdempotencyKey);
                if (itemLines is not null)
                {
                    voucher.SetInvoiceDetails(s.PartyLedgerId!.Value, s.PlaceOfSupplyStateCode, s.SupplyNature!.Value, itemLines);
                }
            }

            await ApplyStatusAsync(voucher, company, device, s, ct);
            db.Vouchers.Add(voucher);
            audit.Record(s.CompanyId, voucher.Status == VoucherStatus.Posted ? "voucher.posted" : "voucher.draft_saved", "voucher", voucher.Id,
                $"{type.Name} {voucher.Number ?? "(draft)"} uploaded from device {device.SeriesCode}", after: SnapshotMapper.ToSnapshot(voucher), changeId: change.ChangeId);
            return voucher;
        }

        if (existing.CompanyId != s.CompanyId)
        {
            throw new SyncRejectedException("sync.id_mismatch", "The voucher belongs to another company.");
        }

        EnsureVersion(existing, change);
        if (existing.Status != VoucherStatus.Draft)
        {
            throw new DomainException("voucher.posted_immutable", "A posted or cancelled voucher cannot be changed; reverse it instead.");
        }

        var before = SnapshotMapper.ToSnapshot(existing);
        if (s.Status == VoucherStatus.Cancelled)
        {
            existing.CancelDraft();
        }
        else
        {
            if (existing.IsInvoice && itemLines is null)
            {
                throw new SyncRejectedException("voucher.invoice_mode_change", "An invoice draft cannot be turned into an accounting voucher.");
            }

            existing.UpdateDraft(company, s.Date, header, lines);
            if (itemLines is not null)
            {
                existing.SetInvoiceDetails(s.PartyLedgerId!.Value, s.PlaceOfSupplyStateCode, s.SupplyNature!.Value, itemLines);
            }

            await ApplyStatusAsync(existing, company, device, s, ct);
        }

        audit.Record(s.CompanyId, existing.Status == VoucherStatus.Posted ? "voucher.posted" : "voucher.draft_updated", "voucher", existing.Id,
            $"{type.Name} {existing.Number ?? "(draft)"} updated from device {device.SeriesCode}", before, SnapshotMapper.ToSnapshot(existing), change.ChangeId);
        return existing;
    }

    private async Task ApplyStatusAsync(Voucher voucher, Company company, Device device, VoucherSnapshot s, CancellationToken ct)
    {
        if (voucher.FinancialYearStart != s.FinancialYearStart)
        {
            throw new SyncRejectedException("voucher.financial_year_mismatch", "The voucher's financial year does not match its date.");
        }

        switch (s.Status)
        {
            case VoucherStatus.Draft:
                await rules.ValidateAsync(voucher, forPosting: false, ct);
                return;
            case VoucherStatus.Cancelled:
                await rules.ValidateAsync(voucher, forPosting: false, ct);
                voucher.CancelDraft();
                return;
            case VoucherStatus.Posted:
                await rules.ValidateAsync(voucher, forPosting: true, ct);
                if (s.SeriesCode != device.SeriesCode || s.SequenceNumber is not { } sequence)
                {
                    throw new SyncRejectedException("voucher.series_not_owned", "Vouchers uploaded by a device must be numbered in that device's own series.");
                }

                var taken = await db.Vouchers.AnyAsync(
                    v => v.CompanyId == s.CompanyId && v.VoucherTypeId == s.VoucherTypeId && v.FinancialYearStart == s.FinancialYearStart
                         && v.SeriesCode == s.SeriesCode && v.SequenceNumber == sequence && v.Id != s.Id,
                    ct);
                if (taken)
                {
                    throw new SyncRejectedException("voucher.number_taken", $"Voucher number {s.Number} is already used.");
                }

                voucher.Post(company, new VoucherNumber(s.SeriesCode, sequence), voucher.CreatedByUserId, s.PostedAtUtc ?? clock.GetUtcNow().UtcDateTime);
                return;
            default:
                throw new SyncRejectedException("voucher.status_invalid", "The voucher status is not valid.");
        }
    }

    private async Task EnsureLedgersPresentAsync(VoucherSnapshot s, CancellationToken ct)
    {
        var items = s.ItemLines ?? [];
        var ids = s.Lines.Select(l => l.LedgerId)
            .Concat(items.Select(l => l.LedgerId))
            .Concat(s.PartyLedgerId is { } party ? [party] : [])
            .Distinct()
            .ToList();
        var found = await db.Ledgers.CountAsync(l => l.CompanyId == s.CompanyId && ids.Contains(l.Id), ct);
        if (found != ids.Count)
        {
            throw new SyncDeferredException("sync.dependency_missing", "A ledger used by this voucher has not been uploaded yet.");
        }

        var itemIds = items.Where(l => l.StockItemId is not null).Select(l => l.StockItemId!.Value).Distinct().ToList();
        if (itemIds.Count > 0 && await db.StockItems.CountAsync(i => i.CompanyId == s.CompanyId && itemIds.Contains(i.Id), ct) != itemIds.Count)
        {
            throw new SyncDeferredException("sync.dependency_missing", "A stock item used by this voucher has not been uploaded yet.");
        }

        var godownIds = items.Where(l => l.GodownId is not null).Select(l => l.GodownId!.Value).Distinct().ToList();
        if (godownIds.Count > 0 && await db.Godowns.CountAsync(g => g.CompanyId == s.CompanyId && godownIds.Contains(g.Id), ct) != godownIds.Count)
        {
            throw new SyncDeferredException("sync.dependency_missing", "A godown used by this voucher has not been uploaded yet.");
        }
    }

    /// <summary>The invoice lines of an uploaded voucher, or null for an accounting voucher.</summary>
    private static List<VoucherItemLineSpec>? ItemLinesOf(VoucherSnapshot s)
    {
        if (s.PartyLedgerId is null)
        {
            if (s.ItemLines is { Count: > 0 })
            {
                throw new SyncRejectedException("invoice.party_required", "An invoice needs a party.");
            }

            return null;
        }

        if (s.SupplyNature is null || s.ItemLines is not { Count: > 0 })
        {
            throw new SyncRejectedException("invoice.incomplete", "The invoice is incomplete.");
        }

        return s.ItemLines.OrderBy(l => l.LineNo)
            .Select(l => new VoucherItemLineSpec(
                l.Id, l.Kind, l.StockItemId, l.GodownId, l.LedgerId, l.Description, l.QuantityMilli, l.RateScaled, l.DiscountMilliPercent,
                Money.FromMinor(l.TaxableMinor), l.Direction,
                new GstRateEntry(s.Date, l.Taxability, l.IgstMilliPercent, l.CessMilliPercent, l.HsnSac),
                new LineTax(Money.FromMinor(l.CgstMinor), Money.FromMinor(l.StateTaxMinor), Money.FromMinor(l.IgstMinor), Money.FromMinor(l.CessMinor))))
            .ToList();
    }

    /// <summary>A reversal's invoice lines must be the original's with the stock movement turned around.</summary>
    private static bool ItemLinesMatch(Voucher derived, VoucherSnapshot s)
    {
        if (derived.PartyLedgerId != s.PartyLedgerId || derived.SupplyNature != s.SupplyNature || derived.PlaceOfSupplyStateCode != s.PlaceOfSupplyStateCode)
        {
            return false;
        }

        var expected = derived.ItemLines.OrderBy(l => l.LineNo)
            .Select(l => (l.Kind, l.StockItemId, l.GodownId, l.LedgerId, l.QuantityMilli, l.RateScaled, l.DiscountMilliPercent, l.TaxableMinor, l.Direction,
                l.Taxability, l.IgstMilliPercent, l.CessMilliPercent, l.CgstMinor, l.StateTaxMinor, l.IgstMinor, l.CessMinor));
        var actual = (s.ItemLines ?? []).OrderBy(l => l.LineNo)
            .Select(l => (l.Kind, l.StockItemId, l.GodownId, l.LedgerId, l.QuantityMilli, l.RateScaled, l.DiscountMilliPercent, l.TaxableMinor, l.Direction,
                l.Taxability, l.IgstMilliPercent, l.CessMilliPercent, l.CgstMinor, l.StateTaxMinor, l.IgstMinor, l.CessMinor));
        return expected.SequenceEqual(actual);
    }

    // ------------------------------------------------------------------ helpers

    private static T Read<T>(SyncChangeUpload change)
    {
        try
        {
            return SyncJson.Deserialize<T>(change.Payload);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new SyncRejectedException("sync.payload_invalid", $"The payload could not be read: {ex.Message}");
        }
    }

    private static void EnsureEnvelope(SyncChangeUpload change, Guid id, Guid companyId)
    {
        if (id != change.EntityId || companyId != change.CompanyId)
        {
            throw new SyncRejectedException("sync.id_mismatch", "The payload does not match the change envelope.");
        }
    }

    private static void EnsureNew(SyncChangeUpload change)
    {
        if (change.BaseVersion != 0)
        {
            // The device believes the row exists on the server, but it does not: treat as a conflict for review.
            throw new SyncConflictException("sync.row_missing", 0, null);
        }
    }

    private static void EnsureVersion(ISyncEntity existing, SyncChangeUpload change)
    {
        if (existing.IsDeleted)
        {
            throw new SyncConflictException("sync.row_deleted", existing.Version, SyncJson.ToElement(SnapshotMapper.ToSnapshot(existing)));
        }

        if (change.BaseVersion != existing.Version)
        {
            throw new SyncConflictException("sync.version_mismatch", existing.Version, SyncJson.ToElement(SnapshotMapper.ToSnapshot(existing)));
        }
    }

    private static void EnsureUniqueName(IEnumerable<AccountGroup> groups, string normalized, Guid id, string entity)
    {
        if (groups.Any(g => !g.IsDeleted && g.Id != id && g.NormalizedName == normalized))
        {
            throw new SyncRejectedException($"{entity}.duplicate_name", $"A {entity} with this name already exists.");
        }
    }

    private async Task EnsureUniqueLedgerNameAsync(Guid companyId, string normalized, Guid id, CancellationToken ct)
    {
        if (await db.Ledgers.AnyAsync(l => l.CompanyId == companyId && !l.IsDeleted && l.Id != id && l.NormalizedName == normalized, ct))
        {
            throw new SyncRejectedException("ledger.duplicate_name", "A ledger with this name already exists.");
        }
    }

    private static CompanyProfile ProfileOf(CompanySnapshot s) => new(
        s.Name, s.MailingName, s.AddressLine1, s.AddressLine2, s.City, s.StateCode, s.Pincode, s.Country, s.Gstin, s.Pan, s.Email, s.Phone);
}
