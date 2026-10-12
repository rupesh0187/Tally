using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Contracts.Sync;

// Full-state snapshots carried in sync payloads (schema version 1).
// Property names intentionally match the persisted entity property names: the persistence layer copies
// snapshots onto entities by name, and round-trip tests guard against drift.
// Amounts are integer minor units so that replication is exact.

public sealed record CompanySnapshot(
    Guid Id,
    string Name,
    string? MailingName,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? StateCode,
    string? Pincode,
    string Country,
    string? Gstin,
    string? Pan,
    string? Email,
    string? Phone,
    string CurrencyCode,
    string CurrencySymbol,
    int FinancialYearStartMonth,
    DateOnly BooksBeginningDate,
    DateOnly? LockedThroughDate,
    GstRegistrationType GstRegistration,
    Guid CreatedByUserId,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record AccountGroupSnapshot(
    Guid Id,
    Guid CompanyId,
    string Name,
    string NormalizedName,
    Guid? ParentGroupId,
    AccountNature Nature,
    bool AffectsGrossProfit,
    string? SystemKey,
    int SortOrder,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record LedgerSnapshot(
    Guid Id,
    Guid CompanyId,
    string Name,
    string NormalizedName,
    string? Alias,
    Guid GroupId,
    long OpeningBalanceMinor,
    DrCr OpeningSide,
    bool IsBillWise,
    string? SystemKey,
    GstRegistrationType GstRegistrationType,
    string? Gstin,
    string? StateCode,
    string? Pan,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Pincode,
    string? Email,
    string? Phone,
    int? CreditPeriodDays,
    GstTaxType GstTaxType,
    IReadOnlyList<GstRateEntry> GstRates,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId,
    IReadOnlyList<OpeningBill>? OpeningBills = null);

public sealed record VoucherTypeSnapshot(
    Guid Id,
    Guid CompanyId,
    string Name,
    string NormalizedName,
    VoucherBaseType BaseType,
    string Abbreviation,
    bool IsSystem,
    bool IsActive,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record VoucherLineSnapshot(
    Guid Id,
    int LineNo,
    Guid LedgerId,
    DrCr Side,
    long AmountMinor,
    string? Narration);

public sealed record VoucherSnapshot(
    Guid Id,
    Guid CompanyId,
    Guid VoucherTypeId,
    VoucherBaseType BaseType,
    DateOnly Date,
    DateOnly FinancialYearStart,
    VoucherStatus Status,
    string? SeriesCode,
    long? SequenceNumber,
    string? Number,
    string? Narration,
    string? Reference,
    DateOnly? ReferenceDate,
    Guid? ReversalOfVoucherId,
    long TotalMinor,
    Guid CreatedByUserId,
    Guid? CreatedOnDeviceId,
    Guid? PostedByUserId,
    DateTime? PostedAtUtc,
    string? IdempotencyKey,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId,
    IReadOnlyList<VoucherLineSnapshot> Lines,
    Guid? PartyLedgerId,
    string? PlaceOfSupplyStateCode,
    SupplyNature? SupplyNature,
    IReadOnlyList<VoucherItemLineSnapshot> ItemLines,
    IReadOnlyList<VoucherBillAllocationSnapshot>? BillAllocations = null);

public sealed record VoucherBillAllocationSnapshot(
    Guid Id,
    int LineNo,
    int AllocationNo,
    BillAllocationKind Kind,
    string? BillName,
    DateOnly? DueDate,
    long AmountMinor);

public sealed record VoucherItemLineSnapshot(
    Guid Id,
    int LineNo,
    InvoiceLineKind Kind,
    Guid? StockItemId,
    Guid? GodownId,
    Guid LedgerId,
    string? Description,
    long QuantityMilli,
    long RateScaled,
    int DiscountMilliPercent,
    long TaxableMinor,
    StockDirection Direction,
    Taxability Taxability,
    int IgstMilliPercent,
    int CessMilliPercent,
    string? HsnSac,
    long CgstMinor,
    long StateTaxMinor,
    long IgstMinor,
    long CessMinor);

public sealed record UnitSnapshot(
    Guid Id,
    Guid CompanyId,
    string Symbol,
    string NormalizedSymbol,
    string Name,
    int DecimalPlaces,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record GodownSnapshot(
    Guid Id,
    Guid CompanyId,
    string Name,
    string NormalizedName,
    bool IsSystem,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record StockItemSnapshot(
    Guid Id,
    Guid CompanyId,
    string Name,
    string NormalizedName,
    string? Alias,
    string? PartNumber,
    Guid UnitId,
    ValuationMethod ValuationMethod,
    IReadOnlyList<GstRateEntry> GstRates,
    long OpeningQuantityMilli,
    long OpeningValueMinor,
    Guid? OpeningGodownId,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);

public sealed record MembershipSnapshot(
    Guid Id,
    Guid CompanyId,
    Guid UserId,
    CompanyRole Role,
    bool IsActive,
    string UserEmail,
    string? UserDisplayName,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId);
