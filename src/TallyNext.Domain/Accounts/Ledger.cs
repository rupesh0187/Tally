using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Tax;

namespace TallyNext.Domain.Accounts;

public enum GstRegistrationType : byte
{
    NotApplicable = 0,
    Regular = 1,
    Composition = 2,
    Unregistered = 3,
    Consumer = 4,
    Overseas = 5,
    SpecialEconomicZone = 6,
}

/// <summary>Party, tax and contact details of a ledger. All optional.</summary>
public sealed record LedgerDetails(
    string? Alias = null,
    bool IsBillWise = false,
    GstRegistrationType GstRegistrationType = GstRegistrationType.NotApplicable,
    string? Gstin = null,
    string? StateCode = null,
    string? Pan = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? Pincode = null,
    string? Email = null,
    string? Phone = null,
    int? CreditPeriodDays = null,
    GstTaxType GstTaxType = GstTaxType.None,
    IReadOnlyList<GstRateEntry>? GstRates = null,
    IReadOnlyList<OpeningBill>? OpeningBills = null);

/// <summary>
/// An account in the books. Opening balances are as at the company's books-beginning date and are only
/// allowed for balance-sheet ledgers (income/expense ledgers start each period from postings).
/// </summary>
public sealed class Ledger : CompanyScopedEntity
{
    private Ledger()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Alias { get; private set; }

    public Guid GroupId { get; private set; }

    /// <summary>Opening balance in minor units; always &gt;= 0, direction given by <see cref="OpeningSide"/>.</summary>
    public long OpeningBalanceMinor { get; private set; }

    public DrCr OpeningSide { get; private set; } = DrCr.Debit;

    public bool IsBillWise { get; private set; }

    public string? SystemKey { get; private set; }

    public GstRegistrationType GstRegistrationType { get; private set; }

    public string? Gstin { get; private set; }

    public string? StateCode { get; private set; }

    public string? Pan { get; private set; }

    public string? AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string? City { get; private set; }

    public string? Pincode { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public int? CreditPeriodDays { get; private set; }

    /// <summary>For duty ledgers (under Duties &amp; Taxes): the GST head they accumulate.</summary>
    public GstTaxType GstTaxType { get; private set; }

    /// <summary>Effective-dated GST rates used when this ledger is billed directly (services, non-stock income).</summary>
    public IReadOnlyList<GstRateEntry> GstRates { get; private set; } = [];

    /// <summary>Bill-wise breakup of the opening balance (bill-wise ledgers only).</summary>
    public IReadOnlyList<OpeningBill> OpeningBills { get; private set; } = [];

    public bool IsSystem => SystemKey is not null;

    public Money OpeningBalance => Money.FromMinor(OpeningBalanceMinor);

    /// <summary>Opening balance with the system sign convention (debit positive, credit negative).</summary>
    public long SignedOpeningMinor => OpeningSide == DrCr.Debit ? OpeningBalanceMinor : -OpeningBalanceMinor;

    public static Ledger Create(
        Guid id,
        AccountGroup group,
        string name,
        Money openingBalance,
        DrCr openingSide,
        LedgerDetails? details = null,
        string? systemKey = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.IsDeleted)
        {
            throw new DomainException("ledger.group_deleted", "The selected group has been deleted.");
        }

        var ledger = new Ledger
        {
            Id = id,
            CompanyId = group.CompanyId,
            GroupId = group.Id,
            SystemKey = systemKey,
        };
        ledger.SetName(name);
        ledger.SetOpening(group, openingBalance, openingSide);
        ledger.SetDetails(details ?? new LedgerDetails());
        return ledger;
    }

    public void Update(AccountGroup group, string name, Money openingBalance, DrCr openingSide, LedgerDetails details)
    {
        ArgumentNullException.ThrowIfNull(group);
        EnsureNotDeleted();
        if (group.CompanyId != CompanyId || group.IsDeleted)
        {
            throw new DomainException("ledger.group_invalid", "The selected group is not valid.");
        }

        if (SystemKey == SystemLedgerKeys.ProfitAndLoss && group.Id != GroupId)
        {
            throw new DomainException("ledger.system_group_fixed", "The Profit & Loss ledger cannot be moved to another group.");
        }

        SetName(name);
        GroupId = group.Id;
        SetOpening(group, openingBalance, openingSide);
        SetDetails(details);
    }

    /// <summary>The caller must have verified that the ledger has no voucher lines.</summary>
    public void MarkDeleted()
    {
        if (IsSystem)
        {
            throw new DomainException("ledger.system_readonly", "Predefined ledgers cannot be deleted.");
        }

        IsDeleted = true;
    }

    public LedgerDetails GetDetails() => new(
        Alias, IsBillWise, GstRegistrationType, Gstin, StateCode, Pan, AddressLine1, AddressLine2, City, Pincode, Email, Phone, CreditPeriodDays, GstTaxType, GstRates, OpeningBills);

    public GstRateEntry? GstRateOn(DateOnly date) => GstRateSchedule.Resolve(GstRates, date);

    private void SetName(string name)
    {
        Name = NameRules.Clean(name, "ledger_name");
        NormalizedName = NameRules.Normalize(Name);
    }

    private void SetOpening(AccountGroup group, Money openingBalance, DrCr openingSide)
    {
        if (openingBalance.IsNegative)
        {
            throw new DomainException("ledger.opening_negative", "Enter the opening balance as a positive amount and choose Dr or Cr.");
        }

        if (!openingSide.IsDefined())
        {
            throw new DomainException("ledger.opening_side_invalid", "The opening balance side must be Dr or Cr.");
        }

        if (!openingBalance.IsZero && !group.Nature.IsBalanceSheet())
        {
            throw new DomainException(
                "ledger.opening_not_allowed",
                "Income and expense ledgers cannot have an opening balance; their balances flow into the Profit & Loss account.");
        }

        OpeningBalanceMinor = openingBalance.Minor;
        OpeningSide = openingSide;
    }

    private void SetDetails(LedgerDetails details)
    {
        var gstin = Companies.Gstin.Normalize(details.Gstin);
        if (gstin is not null && !Companies.Gstin.IsValid(gstin))
        {
            throw new DomainException("ledger.gstin_invalid", "The GSTIN format or check digit is invalid.");
        }

        var stateCode = NameRules.CleanOptional(details.StateCode, 2, "state_code");
        if (stateCode is not null && !IndianStates.IsValidCode(stateCode))
        {
            throw new DomainException("ledger.state_invalid", $"'{stateCode}' is not a valid GST state code.");
        }

        if (gstin is not null)
        {
            var gstinState = Companies.Gstin.StateCodeOf(gstin);
            stateCode ??= gstinState;
            if (!string.Equals(gstinState, stateCode, StringComparison.Ordinal))
            {
                throw new DomainException("ledger.gstin_state_mismatch", "The GSTIN state code does not match the ledger state.");
            }
        }

        var pan = NameRules.CleanOptional(details.Pan, 10, "pan")?.ToUpperInvariant();
        if (pan is not null && !Companies.Pan.IsValid(pan))
        {
            throw new DomainException("ledger.pan_invalid", "The PAN format is invalid.");
        }

        if (details.CreditPeriodDays is < 0 or > 3650)
        {
            throw new DomainException("ledger.credit_period_invalid", "The credit period must be between 0 and 3650 days.");
        }

        if (!Enum.IsDefined(details.GstRegistrationType))
        {
            throw new DomainException("ledger.gst_registration_invalid", "The GST registration type is not valid.");
        }

        if (!Enum.IsDefined(details.GstTaxType))
        {
            throw new DomainException("ledger.gst_tax_type_invalid", "The GST tax type is not valid.");
        }

        if (details.GstTaxType != GstTaxType.None && details.GstRates is { Count: > 0 })
        {
            throw new DomainException("ledger.gst_rates_on_duty_ledger", "A tax ledger cannot carry its own GST rate.");
        }

        Alias = NameRules.CleanOptional(details.Alias, NameRules.MaxLength, "alias");
        IsBillWise = details.IsBillWise;
        GstRegistrationType = gstin is not null && details.GstRegistrationType == GstRegistrationType.NotApplicable
            ? GstRegistrationType.Regular
            : details.GstRegistrationType;
        Gstin = gstin;
        StateCode = stateCode;
        Pan = pan ?? (gstin is not null ? Companies.Gstin.PanOf(gstin) : null);
        AddressLine1 = NameRules.CleanOptional(details.AddressLine1, 200, "address");
        AddressLine2 = NameRules.CleanOptional(details.AddressLine2, 200, "address");
        City = NameRules.CleanOptional(details.City, 100, "city");
        Pincode = NameRules.CleanOptional(details.Pincode, 10, "pincode");
        Email = NameRules.CleanOptional(details.Email, 254, "email");
        Phone = NameRules.CleanOptional(details.Phone, 30, "phone");
        CreditPeriodDays = details.CreditPeriodDays;
        GstTaxType = details.GstTaxType;
        GstRates = GstRateSchedule.Normalize(details.GstRates);
        OpeningBills = OpeningBill.Normalize(details.OpeningBills, details.IsBillWise);
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("ledger.deleted", "The ledger has been deleted.");
        }
    }
}
