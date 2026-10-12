using TallyNext.Domain.Companies;

namespace TallyNext.Contracts.Companies;

public sealed record CompanySummaryDto(
    Guid Id,
    string Name,
    string? StateCode,
    string? Gstin,
    string CurrencyCode,
    CompanyRole Role,
    DateOnly BooksBeginningDate,
    int FinancialYearStartMonth);

public sealed record CompanyDto(
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
    long Version);

/// <summary>Editable company details (form model).</summary>
public class CompanyProfileInput
{
    public string Name { get; set; } = string.Empty;

    public string? MailingName { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? StateCode { get; set; }

    public string? Pincode { get; set; }

    public string Country { get; set; } = "India";

    public string? Gstin { get; set; }

    public string? Pan { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }
}

public sealed class CreateCompanyRequest : CompanyProfileInput
{
    /// <summary>Optional client-generated id (UUIDv7) so that retries are idempotent.</summary>
    public Guid? Id { get; set; }

    public string CurrencyCode { get; set; } = "INR";

    public int FinancialYearStartMonth { get; set; } = 4;

    public DateOnly BooksBeginningDate { get; set; }
}

public sealed class UpdateCompanyRequest : CompanyProfileInput
{
    /// <summary>When supplied, the update fails with a conflict if the company changed since this version.</summary>
    public long? ExpectedVersion { get; set; }
}

public sealed class SetPeriodLockRequest
{
    /// <summary>Lock vouchers dated on or before this date. Null unlocks.</summary>
    public DateOnly? LockedThroughDate { get; set; }
}

public sealed record MemberDto(Guid MembershipId, Guid UserId, string Email, string? DisplayName, CompanyRole Role, bool IsActive);

public sealed class AddMemberRequest
{
    public string Email { get; set; } = string.Empty;

    public CompanyRole Role { get; set; } = CompanyRole.Viewer;
}

public sealed class ChangeMemberRoleRequest
{
    public CompanyRole Role { get; set; }
}

public sealed record StateDto(string Code, string Name);

public sealed record CurrencyDto(string Code, string Symbol, string Name);
