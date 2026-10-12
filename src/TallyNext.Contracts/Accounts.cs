using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Contracts.Accounts;

public sealed record AccountGroupDto(
    Guid Id,
    string Name,
    Guid? ParentGroupId,
    string? ParentName,
    AccountNature Nature,
    bool AffectsGrossProfit,
    bool IsSystem,
    int Depth,
    int LedgerCount,
    long Version);

public sealed class AccountGroupInput
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Null creates a primary group, which then requires <see cref="Nature"/>.</summary>
    public Guid? ParentGroupId { get; set; }

    public AccountNature? Nature { get; set; }

    public bool AffectsGrossProfit { get; set; }

    public long? ExpectedVersion { get; set; }
}

public sealed record LedgerDto(
    Guid Id,
    string Name,
    string? Alias,
    Guid GroupId,
    string GroupName,
    AccountNature Nature,
    LedgerTraits Traits,
    decimal OpeningBalance,
    DrCr OpeningSide,
    bool IsBillWise,
    bool IsSystem,
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
    long Version);

/// <summary>Compact projection used for type-to-search ledger pickers and cached on the client.</summary>
public sealed record LedgerLookupItem(
    Guid Id,
    string Name,
    string? Alias,
    Guid GroupId,
    string GroupName,
    AccountNature Nature,
    LedgerTraits Traits);

public sealed class LedgerInput
{
    public string Name { get; set; } = string.Empty;

    public string? Alias { get; set; }

    public Guid GroupId { get; set; }

    public decimal OpeningBalance { get; set; }

    public DrCr OpeningSide { get; set; } = DrCr.Debit;

    public bool IsBillWise { get; set; }

    public GstRegistrationType GstRegistrationType { get; set; }

    public string? Gstin { get; set; }

    public string? StateCode { get; set; }

    public string? Pan { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? Pincode { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public int? CreditPeriodDays { get; set; }

    /// <summary>Optional client-generated id for idempotent creates.</summary>
    public Guid? Id { get; set; }

    public long? ExpectedVersion { get; set; }
}

public sealed class LedgerQuery
{
    public string? Search { get; set; }

    public Guid? GroupId { get; set; }

    public bool CashOrBankOnly { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = Paging.DefaultPageSize;
}
