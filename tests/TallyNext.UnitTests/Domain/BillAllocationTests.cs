using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.UnitTests.Domain;

public sealed class BillAllocationTests
{
    private static readonly DateOnly Day = new(2026, 5, 10);
    private readonly Company _company = Company.Create(Ids.New(), new CompanyProfile("Synthetic Co"), "INR", 4, new DateOnly(2026, 4, 1), Guid.NewGuid());
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _sales = Guid.NewGuid();

    private static BillAllocationSpec Bill(BillAllocationKind kind, string? name, decimal amount, DateOnly? due = null) =>
        new(kind, name, due, Money.FromDecimal(amount));

    private Voucher Sale(decimal amount, params BillAllocationSpec[] bills) => Voucher.CreateDraft(
        Ids.New(), _company, VoucherType.CreateSystem(Ids.New(), _company.Id, VoucherBaseType.Sales, "Sales", "S"), Day, new VoucherHeader(),
        [
            new VoucherLineSpec(_customer, DrCr.Debit, Money.FromDecimal(amount), Bills: bills),
            new VoucherLineSpec(_sales, DrCr.Credit, Money.FromDecimal(amount)),
        ],
        Guid.NewGuid(), null);

    [Fact]
    public void Allocations_must_add_up_to_the_line_amount()
    {
        var ex = Assert.Throws<DomainException>(() => Sale(1_000m, Bill(BillAllocationKind.NewRef, "INV-1", 600m), Bill(BillAllocationKind.OnAccount, null, 300m)));
        Assert.Equal("bill.allocation_mismatch", ex.Code);

        var voucher = Sale(1_000m, Bill(BillAllocationKind.NewRef, "INV-1", 700m), Bill(BillAllocationKind.OnAccount, null, 300m));
        Assert.Equal(2, voucher.BillAllocations.Count);
        Assert.All(voucher.BillAllocations, a => Assert.Equal((_customer, DrCr.Debit, 1), (a.LedgerId, a.Side, a.LineNo)));
    }

    [Theory]
    [InlineData(BillAllocationKind.OnAccount, "X", "bill.on_account_named")]
    [InlineData(BillAllocationKind.AgstRef, null, "bill.name_required")]
    [InlineData(BillAllocationKind.Advance, "  ", "bill.name_required")]
    public void Names_are_required_exactly_where_they_mean_something(BillAllocationKind kind, string? name, string code)
    {
        Assert.Equal(code, Assert.Throws<DomainException>(() => Sale(100m, Bill(kind, name, 100m))).Code);
    }

    [Fact]
    public void Amounts_must_be_positive_and_settlements_have_no_due_date()
    {
        Assert.Equal("bill.amount_invalid", Assert.Throws<DomainException>(() => Sale(100m, Bill(BillAllocationKind.NewRef, "A", 100m), Bill(BillAllocationKind.OnAccount, null, 0m))).Code);
        Assert.Equal("bill.due_date_not_allowed", Assert.Throws<DomainException>(() => Sale(100m, Bill(BillAllocationKind.AgstRef, "A", 100m, Day))).Code);
    }

    [Fact]
    public void Posting_names_an_unnamed_new_bill_after_the_voucher_number()
    {
        var voucher = Sale(500m, Bill(BillAllocationKind.NewRef, null, 300m, Day.AddDays(30)), Bill(BillAllocationKind.NewRef, " po  77 ", 200m));
        voucher.Post(_company, new VoucherNumber("D2", 9), Guid.NewGuid(), DateTime.UtcNow);

        Assert.Equal(["D2/9", "po 77"], voucher.BillAllocations.Select(a => a.BillName));
        Assert.Equal(["D2/9", "PO 77"], voucher.BillAllocations.Select(a => a.NormalizedBillName));
        Assert.All(voucher.BillAllocations, a => Assert.True(a.IsPosted));
    }

    [Fact]
    public void A_reversal_settles_the_same_bills_on_the_opposite_side()
    {
        var original = Sale(500m, Bill(BillAllocationKind.NewRef, "INV-9", 400m, Day.AddDays(15)), Bill(BillAllocationKind.OnAccount, null, 100m));
        original.Post(_company, new VoucherNumber("D2", 10), Guid.NewGuid(), DateTime.UtcNow);

        var reversal = Voucher.CreateReversal(Ids.New(), _company, original, Day.AddDays(1), null, Guid.NewGuid(), null);

        Assert.Equal(
            [(BillAllocationKind.AgstRef, "INV-9", 40_000L, DrCr.Credit), (BillAllocationKind.OnAccount, (string?)null, 10_000L, DrCr.Credit)],
            reversal.BillAllocations.Select(a => (a.Kind, a.BillName, a.AmountMinor, a.Side)));
        Assert.All(reversal.BillAllocations, a => Assert.Null(a.DueDate));
        Assert.Equal(0, original.BillAllocations.Sum(a => a.SignedAmountMinor) + reversal.BillAllocations.Sum(a => a.SignedAmountMinor));
    }

    [Fact]
    public void Opening_bills_belong_to_bill_wise_ledgers_and_have_unique_references()
    {
        var debtors = AccountGroup.CreatePrimary(Ids.New(), _company.Id, "Debtors (test)", AccountNature.Asset, false);
        OpeningBill Opening(string name) => new(name, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 1_000, DrCr.Debit);

        Assert.Equal("ledger.opening_bills_not_billwise", Assert.Throws<DomainException>(() =>
            Ledger.Create(Ids.New(), debtors, "Party A", Money.FromDecimal(10m), DrCr.Debit, new LedgerDetails(IsBillWise: false, OpeningBills: [Opening("OB-1")]))).Code);
        Assert.Equal("ledger.opening_bill_duplicate", Assert.Throws<DomainException>(() =>
            Ledger.Create(Ids.New(), debtors, "Party B", Money.FromDecimal(20m), DrCr.Debit, new LedgerDetails(IsBillWise: true, OpeningBills: [Opening("OB-1"), Opening("ob-1")]))).Code);

        var ledger = Ledger.Create(Ids.New(), debtors, "Party C", Money.FromDecimal(20m), DrCr.Debit, new LedgerDetails(IsBillWise: true, OpeningBills: [Opening(" OB-2 ")]));
        Assert.Equal("OB-2", Assert.Single(ledger.OpeningBills).Name);
    }
}
