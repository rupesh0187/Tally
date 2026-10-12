using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Application.Sync;

/// <summary>
/// Maps aggregates to their replication snapshots. Used for the server change log, the device outbox and
/// audit before/after images, so all three describe an entity identically.
/// </summary>
public static class SnapshotMapper
{
    public static string EntityTypeOf(ISyncEntity entity) => entity switch
    {
        Company => SyncEntityTypes.Company,
        AccountGroup => SyncEntityTypes.AccountGroup,
        Ledger => SyncEntityTypes.Ledger,
        VoucherType => SyncEntityTypes.VoucherType,
        Voucher => SyncEntityTypes.Voucher,
        CompanyMembership => SyncEntityTypes.Membership,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity.GetType().Name, "Not a replicated entity type."),
    };

    public static object ToSnapshot(ISyncEntity entity) => entity switch
    {
        Company c => ToSnapshot(c),
        AccountGroup g => ToSnapshot(g),
        Ledger l => ToSnapshot(l),
        VoucherType t => ToSnapshot(t),
        Voucher v => ToSnapshot(v),
        CompanyMembership m => ToSnapshot(m),
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity.GetType().Name, "Not a replicated entity type."),
    };

    public static CompanySnapshot ToSnapshot(Company c) => new(
        c.Id, c.Name, c.MailingName, c.AddressLine1, c.AddressLine2, c.City, c.StateCode, c.Pincode, c.Country,
        c.Gstin, c.Pan, c.Email, c.Phone, c.CurrencyCode, c.CurrencySymbol, c.FinancialYearStartMonth,
        c.BooksBeginningDate, c.LockedThroughDate, c.CreatedByUserId, c.IsDeleted, c.CreatedAtUtc, c.UpdatedAtUtc, c.UpdatedByUserId);

    public static AccountGroupSnapshot ToSnapshot(AccountGroup g) => new(
        g.Id, g.CompanyId, g.Name, g.NormalizedName, g.ParentGroupId, g.Nature, g.AffectsGrossProfit, g.SystemKey,
        g.SortOrder, g.IsDeleted, g.CreatedAtUtc, g.UpdatedAtUtc, g.UpdatedByUserId);

    public static LedgerSnapshot ToSnapshot(Ledger l) => new(
        l.Id, l.CompanyId, l.Name, l.NormalizedName, l.Alias, l.GroupId, l.OpeningBalanceMinor, l.OpeningSide, l.IsBillWise,
        l.SystemKey, l.GstRegistrationType, l.Gstin, l.StateCode, l.Pan, l.AddressLine1, l.AddressLine2, l.City, l.Pincode,
        l.Email, l.Phone, l.CreditPeriodDays, l.IsDeleted, l.CreatedAtUtc, l.UpdatedAtUtc, l.UpdatedByUserId);

    public static VoucherTypeSnapshot ToSnapshot(VoucherType t) => new(
        t.Id, t.CompanyId, t.Name, t.NormalizedName, t.BaseType, t.Abbreviation, t.IsSystem, t.IsActive, t.IsDeleted,
        t.CreatedAtUtc, t.UpdatedAtUtc, t.UpdatedByUserId);

    public static VoucherSnapshot ToSnapshot(Voucher v) => new(
        v.Id, v.CompanyId, v.VoucherTypeId, v.BaseType, v.Date, v.FinancialYearStart, v.Status, v.SeriesCode, v.SequenceNumber,
        v.Number, v.Narration, v.Reference, v.ReferenceDate, v.ReversalOfVoucherId, v.TotalMinor, v.CreatedByUserId,
        v.CreatedOnDeviceId, v.PostedByUserId, v.PostedAtUtc, v.IdempotencyKey, v.IsDeleted, v.CreatedAtUtc, v.UpdatedAtUtc,
        v.UpdatedByUserId,
        v.Lines.OrderBy(l => l.LineNo).Select(l => new VoucherLineSnapshot(l.Id, l.LineNo, l.LedgerId, l.Side, l.AmountMinor, l.Narration)).ToList());

    public static MembershipSnapshot ToSnapshot(CompanyMembership m) => new(
        m.Id, m.CompanyId, m.UserId, m.Role, m.IsActive, m.UserEmail, m.UserDisplayName, m.IsDeleted,
        m.CreatedAtUtc, m.UpdatedAtUtc, m.UpdatedByUserId);
}
