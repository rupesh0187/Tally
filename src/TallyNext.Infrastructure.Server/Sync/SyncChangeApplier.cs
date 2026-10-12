using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
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

        if (ProfileOf(s) != existing.GetProfile())
        {
            await guard.RequireAsync(s.Id, Permission.CompanyManage, ct);
            existing.UpdateProfile(ProfileOf(s));
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
            s.City, s.Pincode, s.Email, s.Phone, s.CreditPeriodDays);

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
            if (await db.VoucherLines.AnyAsync(l => l.CompanyId == s.CompanyId && l.LedgerId == existing.Id, ct))
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

        var existing = await db.Vouchers.Include(v => v.Lines).SingleOrDefaultAsync(v => v.Id == s.Id, ct);
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
                var original = await db.Vouchers.Include(v => v.Lines).SingleOrDefaultAsync(v => v.CompanyId == s.CompanyId && v.Id == originalId, ct)
                    ?? throw new SyncDeferredException("sync.dependency_missing", "The reversed voucher has not been uploaded yet.");
                if (await db.Vouchers.AnyAsync(v => v.ReversalOfVoucherId == originalId, ct))
                {
                    throw new SyncRejectedException("voucher.already_reversed", "This voucher has already been reversed (possibly from another device).");
                }

                voucher = Voucher.CreateReversal(s.Id, company, original, s.Date, s.Narration, userId, device.Id);
                var expected = voucher.Lines.Select(l => (l.LedgerId, l.Side, l.AmountMinor)).Order().ToList();
                var actual = s.Lines.Select(l => (l.LedgerId, l.Side, l.AmountMinor)).Order().ToList();
                if (!expected.SequenceEqual(actual))
                {
                    throw new SyncRejectedException("voucher.reversal_mismatch", "The reversal does not mirror the original voucher.");
                }
            }
            else
            {
                voucher = Voucher.CreateDraft(s.Id, company, type, s.Date, header, lines, userId, device.Id, s.IdempotencyKey);
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
            existing.UpdateDraft(company, s.Date, header, lines);
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
        var ids = s.Lines.Select(l => l.LedgerId).Distinct().ToList();
        var found = await db.Ledgers.CountAsync(l => l.CompanyId == s.CompanyId && ids.Contains(l.Id), ct);
        if (found != ids.Count)
        {
            throw new SyncDeferredException("sync.dependency_missing", "A ledger used by this voucher has not been uploaded yet.");
        }
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
