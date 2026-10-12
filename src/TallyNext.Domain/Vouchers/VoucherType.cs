using TallyNext.Domain.Common;

namespace TallyNext.Domain.Vouchers;

/// <summary>
/// The accounting behaviour of a voucher. Keyboard shortcuts in the UI map to these
/// (Contra F4, Payment F5, Receipt F6, Journal F7, Sales F8, Purchase F9, Credit Note Alt+F6, Debit Note Alt+F5).
/// </summary>
public enum VoucherBaseType : byte
{
    Contra = 1,
    Payment = 2,
    Receipt = 3,
    Journal = 4,
    Sales = 5,
    Purchase = 6,
    CreditNote = 7,
    DebitNote = 8,
}

/// <summary>
/// A named voucher type. Each company gets one system type per <see cref="VoucherBaseType"/>; users may add
/// their own (for example "Bank Payment") that behave like their base type but number independently.
/// </summary>
public sealed class VoucherType : CompanyScopedEntity
{
    private VoucherType()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public VoucherBaseType BaseType { get; private set; }

    public string Abbreviation { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public bool IsActive { get; private set; } = true;

    public static VoucherType CreateSystem(Guid id, Guid companyId, VoucherBaseType baseType, string name, string abbreviation)
    {
        var type = Create(id, companyId, baseType, name, abbreviation);
        type.IsSystem = true;
        return type;
    }

    public static VoucherType Create(Guid id, Guid companyId, VoucherBaseType baseType, string name, string abbreviation)
    {
        if (!Enum.IsDefined(baseType))
        {
            throw new DomainException("voucher_type.base_invalid", "The base voucher type is not valid.");
        }

        var type = new VoucherType { Id = id, CompanyId = companyId, BaseType = baseType, IsActive = true };
        type.SetNames(name, abbreviation);
        return type;
    }

    public void Update(string name, string abbreviation, bool isActive)
    {
        if (IsSystem && !isActive)
        {
            throw new DomainException("voucher_type.system_required", "Predefined voucher types cannot be deactivated.");
        }

        SetNames(name, abbreviation);
        IsActive = isActive;
    }

    private void SetNames(string name, string abbreviation)
    {
        Name = NameRules.Clean(name, "voucher_type_name");
        NormalizedName = NameRules.Normalize(Name);
        var abbr = NameRules.Clean(abbreviation, "abbreviation");
        if (abbr.Length > 10)
        {
            throw new DomainException("abbreviation.too_long", "The abbreviation must be at most 10 characters.");
        }

        Abbreviation = abbr;
    }
}
