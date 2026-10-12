using TallyNext.Domain.Common;

namespace TallyNext.Domain.Companies;

/// <summary>Editable descriptive details of a company.</summary>
public sealed record CompanyProfile(
    string Name,
    string? MailingName = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? StateCode = null,
    string? Pincode = null,
    string Country = "India",
    string? Gstin = null,
    string? Pan = null,
    string? Email = null,
    string? Phone = null);

/// <summary>
/// A set of books. The company is the tenant / data-isolation boundary: every accounting row carries
/// its CompanyId and every query is scoped by it.
/// </summary>
/// <remarks>
/// Currency, financial-year start month and books-beginning date are fixed at creation in this version
/// because voucher numbering, opening balances and report periods depend on them.
/// </remarks>
public sealed class Company : ISyncEntity
{
    private Company()
    {
    }

    public Guid Id { get; private set; }

    Guid ISyncEntity.CompanyId => Id;

    public long Version { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? MailingName { get; private set; }

    public string? AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string? City { get; private set; }

    public string? StateCode { get; private set; }

    public string? Pincode { get; private set; }

    public string Country { get; private set; } = "India";

    public string? Gstin { get; private set; }

    public string? Pan { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string CurrencyCode { get; private set; } = "INR";

    public string CurrencySymbol { get; private set; } = "₹";

    /// <summary>Month (1-12) on which each financial year starts; 4 (April) for Indian companies.</summary>
    public int FinancialYearStartMonth { get; private set; } = 4;

    /// <summary>Opening balances are as at this date; vouchers dated earlier are rejected.</summary>
    public DateOnly BooksBeginningDate { get; private set; }

    /// <summary>Vouchers dated on or before this date can no longer be posted or reversed.</summary>
    public DateOnly? LockedThroughDate { get; private set; }

    public bool IsIndian => string.Equals(Country, "India", StringComparison.OrdinalIgnoreCase);

    public static Company Create(
        Guid id,
        CompanyProfile profile,
        string currencyCode,
        int financialYearStartMonth,
        DateOnly booksBeginningDate,
        Guid createdByUserId)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("company.id_required", "A company identifier is required.");
        }

        var currency = Currencies.Find(currencyCode)
            ?? throw new DomainException("company.currency_unsupported", $"Currency '{currencyCode}' is not supported.");

        if (financialYearStartMonth is < 1 or > 12)
        {
            throw new DomainException("company.fy_month_invalid", "The financial year start month must be between 1 and 12.");
        }

        if (booksBeginningDate.Year < 1990 || booksBeginningDate.Year > 2200)
        {
            throw new DomainException("company.books_beginning_invalid", "The books beginning date is outside the supported range.");
        }

        var company = new Company
        {
            Id = id,
            CurrencyCode = currency.Code,
            CurrencySymbol = currency.Symbol,
            FinancialYearStartMonth = financialYearStartMonth,
            BooksBeginningDate = booksBeginningDate,
            CreatedByUserId = createdByUserId,
        };
        company.UpdateProfile(profile);
        return company;
    }

    public void UpdateProfile(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureNotDeleted();

        var name = NameRules.Clean(profile.Name, "company_name");
        var country = NameRules.CleanOptional(profile.Country, 60, "country") ?? "India";
        var isIndian = string.Equals(country, "India", StringComparison.OrdinalIgnoreCase);

        var stateCode = NameRules.CleanOptional(profile.StateCode, 2, "state_code");
        if (isIndian && stateCode is not null && !IndianStates.IsValidCode(stateCode))
        {
            throw new DomainException("company.state_invalid", $"'{stateCode}' is not a valid GST state code.");
        }

        var gstin = Companies.Gstin.Normalize(profile.Gstin);
        if (gstin is not null)
        {
            if (!isIndian)
            {
                throw new DomainException("company.gstin_not_applicable", "A GSTIN applies only to companies in India.");
            }

            if (!Companies.Gstin.IsValid(gstin))
            {
                throw new DomainException("company.gstin_invalid", "The GSTIN format or check digit is invalid.");
            }

            var gstinState = Companies.Gstin.StateCodeOf(gstin);
            stateCode ??= gstinState;
            if (!string.Equals(gstinState, stateCode, StringComparison.Ordinal))
            {
                throw new DomainException("company.gstin_state_mismatch", "The GSTIN state code does not match the company state.");
            }
        }

        var pan = NameRules.CleanOptional(profile.Pan, 10, "pan")?.ToUpperInvariant();
        if (pan is not null && !Companies.Pan.IsValid(pan))
        {
            throw new DomainException("company.pan_invalid", "The PAN format is invalid.");
        }

        if (gstin is not null && pan is not null && !string.Equals(Companies.Gstin.PanOf(gstin), pan, StringComparison.Ordinal))
        {
            throw new DomainException("company.gstin_pan_mismatch", "The PAN does not match the PAN embedded in the GSTIN.");
        }

        var pincode = NameRules.CleanOptional(profile.Pincode, 10, "pincode");
        if (isIndian && pincode is not null && (pincode.Length != 6 || !pincode.All(char.IsAsciiDigit) || pincode[0] == '0'))
        {
            throw new DomainException("company.pincode_invalid", "An Indian PIN code has six digits and does not start with 0.");
        }

        Name = name;
        MailingName = NameRules.CleanOptional(profile.MailingName, 200, "mailing_name");
        AddressLine1 = NameRules.CleanOptional(profile.AddressLine1, 200, "address");
        AddressLine2 = NameRules.CleanOptional(profile.AddressLine2, 200, "address");
        City = NameRules.CleanOptional(profile.City, 100, "city");
        StateCode = stateCode;
        Pincode = pincode;
        Country = country;
        Gstin = gstin;
        Pan = pan ?? (gstin is not null ? Companies.Gstin.PanOf(gstin) : null);
        Email = NameRules.CleanOptional(profile.Email, 254, "email");
        Phone = NameRules.CleanOptional(profile.Phone, 30, "phone");
    }

    public CompanyProfile GetProfile() => new(
        Name, MailingName, AddressLine1, AddressLine2, City, StateCode, Pincode, Country, Gstin, Pan, Email, Phone);

    /// <summary>Locks the books through the given date (inclusive). <c>null</c> removes the lock.</summary>
    public void SetLockedThrough(DateOnly? lockedThrough)
    {
        EnsureNotDeleted();
        if (lockedThrough is { } date && date < BooksBeginningDate.AddDays(-1))
        {
            throw new DomainException("company.lock_before_books", "The lock date cannot be earlier than the books beginning date.");
        }

        LockedThroughDate = lockedThrough;
    }

    public FinancialYear FinancialYearOf(DateOnly date) => FinancialYear.Containing(date, FinancialYearStartMonth);

    public FinancialYear CurrentFinancialYear(DateOnly today) => FinancialYearOf(today);

    /// <summary>Throws unless a voucher dated <paramref name="date"/> may be posted or reversed.</summary>
    public void EnsureDateIsOpen(DateOnly date)
    {
        if (date < BooksBeginningDate)
        {
            throw new DomainException(
                "voucher.before_books_beginning",
                $"The date {date:yyyy-MM-dd} is before the books beginning date {BooksBeginningDate:yyyy-MM-dd}.");
        }

        if (LockedThroughDate is { } locked && date <= locked)
        {
            throw new DomainException(
                "voucher.period_locked",
                $"The books are locked through {locked:yyyy-MM-dd}; vouchers on or before that date cannot be changed.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("company.deleted", "The company has been deleted.");
        }
    }
}

/// <summary>Permanent Account Number structural validation (AAAAA9999A).</summary>
public static class Pan
{
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 10)
        {
            return false;
        }

        for (var i = 0; i < 10; i++)
        {
            var ch = value[i];
            var ok = i is >= 5 and <= 8 ? char.IsAsciiDigit(ch) : char.IsAsciiLetterUpper(ch);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
