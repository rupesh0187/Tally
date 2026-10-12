using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;

namespace TallyNext.UnitTests.Domain;

public sealed class CompanyTests
{
    [Theory]
    [InlineData("27AAPFU0939F1ZV")]
    [InlineData("29AAGCB7383J1Z4")]
    public void Gstin_accepts_valid_check_digits(string gstin) => Assert.True(Gstin.IsValid(gstin));

    [Theory]
    [InlineData("27AAPFU0939F1ZA")]
    [InlineData("27AAPFU0939F1Z")]
    [InlineData("2AAAPFU0939F1ZV")]
    [InlineData("")]
    [InlineData(null)]
    public void Gstin_rejects_bad_format_or_check_digit(string? gstin) => Assert.False(Gstin.IsValid(gstin));

    [Fact]
    public void Gstin_check_character_is_computed_from_first_fourteen()
    {
        Assert.Equal('V', Gstin.ComputeCheckCharacter("27AAPFU0939F1Z"));
    }

    [Theory]
    [InlineData(2026, 4, 1, 2026, "2026-27")]
    [InlineData(2027, 3, 31, 2026, "2026-27")]
    [InlineData(2027, 1, 15, 2026, "2026-27")]
    [InlineData(2026, 12, 31, 2026, "2026-27")]
    public void Financial_year_defaults_to_april_march(int y, int m, int d, int expectedStartYear, string label)
    {
        var fy = FinancialYear.Containing(new DateOnly(y, m, d), 4);
        Assert.Equal(new DateOnly(expectedStartYear, 4, 1), fy.Start);
        Assert.Equal(new DateOnly(expectedStartYear + 1, 3, 31), fy.End);
        Assert.Equal(label, fy.Label);
    }

    [Fact]
    public void Calendar_financial_year_is_supported()
    {
        var fy = FinancialYear.Containing(new DateOnly(2026, 7, 9), 1);
        Assert.Equal(new DateOnly(2026, 1, 1), fy.Start);
        Assert.Equal("2026", fy.Label);
    }

    [Fact]
    public void Company_rejects_gstin_from_a_different_state()
    {
        var ex = Assert.Throws<DomainException>(() => Company.Create(
            Ids.New(), new CompanyProfile("Synthetic Co", StateCode: "29", Gstin: "27AAPFU0939F1ZV"), "INR", 4, new DateOnly(2026, 4, 1), Guid.NewGuid()));
        Assert.Equal("company.gstin_state_mismatch", ex.Code);
    }

    [Fact]
    public void Company_derives_state_and_pan_from_gstin()
    {
        var company = Company.Create(Ids.New(), new CompanyProfile("Synthetic Co", Gstin: "27aapfu0939f1zv"), "INR", 4, new DateOnly(2026, 4, 1), Guid.NewGuid());
        Assert.Equal("27", company.StateCode);
        Assert.Equal("AAPFU0939F", company.Pan);
        Assert.Equal("27AAPFU0939F1ZV", company.Gstin);
    }

    [Fact]
    public void Company_rejects_unsupported_three_decimal_currency()
    {
        var ex = Assert.Throws<DomainException>(() => Company.Create(Ids.New(), new CompanyProfile("X"), "KWD", 4, new DateOnly(2026, 4, 1), Guid.NewGuid()));
        Assert.Equal("company.currency_unsupported", ex.Code);
    }

    [Fact]
    public void Period_lock_closes_dates_on_or_before_lock_date()
    {
        var company = Company.Create(Ids.New(), new CompanyProfile("X"), "INR", 4, new DateOnly(2026, 4, 1), Guid.NewGuid());
        company.SetLockedThrough(new DateOnly(2026, 6, 30));
        Assert.Equal("voucher.period_locked", Assert.Throws<DomainException>(() => company.EnsureDateIsOpen(new DateOnly(2026, 6, 30))).Code);
        company.EnsureDateIsOpen(new DateOnly(2026, 7, 1));
        Assert.Equal("voucher.before_books_beginning", Assert.Throws<DomainException>(() => company.EnsureDateIsOpen(new DateOnly(2026, 3, 31))).Code);
    }

    [Fact]
    public void Role_matrix_restricts_viewers_and_data_entry()
    {
        Assert.True(RolePermissions.Has(CompanyRole.Owner, Permission.MemberManage));
        Assert.False(RolePermissions.Has(CompanyRole.Accountant, Permission.MemberManage));
        Assert.True(RolePermissions.Has(CompanyRole.DataEntry, Permission.VoucherCreate));
        Assert.False(RolePermissions.Has(CompanyRole.DataEntry, Permission.VoucherReverse));
        Assert.False(RolePermissions.Has(CompanyRole.Viewer, Permission.VoucherCreate));
        Assert.True(RolePermissions.Has(CompanyRole.Viewer, Permission.ReportView));
    }

    [Fact]
    public void Membership_ids_are_deterministic_per_company_and_user()
    {
        var company = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var a = CompanyMembership.Create(company, user, CompanyRole.Owner, "a@example.test", null);
        var b = CompanyMembership.Create(company, user, CompanyRole.Viewer, "a@example.test", null);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(8, a.Id.Version);
        Assert.NotEqual(a.Id, CompanyMembership.IdFor(company, Guid.CreateVersion7()));
    }
}
