using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;

namespace TallyNext.UnitTests.Domain;

public sealed class VoucherTests
{
    private static readonly DateOnly Day = new(2026, 5, 10);
    private readonly Company _company = Company.Create(Ids.New(), new CompanyProfile("Synthetic Co"), "INR", 4, new DateOnly(2026, 4, 1), Guid.NewGuid());
    private readonly Guid _user = Guid.NewGuid();

    private VoucherType Type(VoucherBaseType baseType) => VoucherType.CreateSystem(Ids.New(), _company.Id, baseType, baseType.ToString(), "X");

    private Voucher Draft(VoucherBaseType type, params (Guid Ledger, DrCr Side, decimal Amount)[] lines) =>
        Voucher.CreateDraft(Ids.New(), _company, Type(type), Day, new VoucherHeader("n"), lines.Select(l => new VoucherLineSpec(l.Ledger, l.Side, Money.FromDecimal(l.Amount))).ToList(), _user, null);

    [Fact]
    public void Balanced_voucher_can_be_posted_and_is_numbered()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var voucher = Draft(VoucherBaseType.Journal, (a, DrCr.Debit, 100.50m), (b, DrCr.Credit, 100.50m));

        voucher.Post(_company, new VoucherNumber("D1", 7), _user, DateTime.UtcNow);

        Assert.Equal(VoucherStatus.Posted, voucher.Status);
        Assert.Equal("D1/7", voucher.Number);
        Assert.Equal(10050, voucher.TotalMinor);
        Assert.All(voucher.Lines, l => Assert.True(l.IsPosted));
        Assert.Equal(new DateOnly(2026, 4, 1), voucher.FinancialYearStart);
    }

    [Fact]
    public void Server_series_numbers_display_without_prefix()
    {
        Assert.Equal("12", new VoucherNumber(VoucherNumber.ServerSeriesCode, 12).Display);
    }

    [Fact]
    public void Unbalanced_voucher_is_rejected()
    {
        var voucher = Draft(VoucherBaseType.Journal, (Guid.NewGuid(), DrCr.Debit, 100m), (Guid.NewGuid(), DrCr.Credit, 99.99m));
        var ex = Assert.Throws<DomainException>(() => voucher.Post(_company, new VoucherNumber("D1", 1), _user, DateTime.UtcNow));
        Assert.Equal("voucher.unbalanced", ex.Code);
        Assert.Equal(VoucherStatus.Draft, voucher.Status);
        Assert.Null(voucher.Number);
    }

    [Fact]
    public void One_sided_voucher_is_rejected()
    {
        var voucher = Draft(VoucherBaseType.Journal, (Guid.NewGuid(), DrCr.Debit, 100m), (Guid.NewGuid(), DrCr.Debit, 100m));
        Assert.Equal("voucher.one_sided", Assert.Throws<DomainException>(() => voucher.EnsurePostable()).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_line_amounts_are_rejected(int amount)
    {
        var ex = Assert.Throws<DomainException>(() => Draft(VoucherBaseType.Journal, (Guid.NewGuid(), DrCr.Debit, amount), (Guid.NewGuid(), DrCr.Credit, 1m)));
        Assert.Equal("voucher.line_amount_invalid", ex.Code);
    }

    [Fact]
    public void Posted_voucher_cannot_be_edited_or_cancelled()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var voucher = Draft(VoucherBaseType.Journal, (a, DrCr.Debit, 10m), (b, DrCr.Credit, 10m));
        voucher.Post(_company, new VoucherNumber("D1", 1), _user, DateTime.UtcNow);

        Assert.Equal("voucher.posted_immutable", Assert.Throws<DomainException>(() =>
            voucher.UpdateDraft(_company, Day, new VoucherHeader(), [new VoucherLineSpec(a, DrCr.Debit, Money.FromDecimal(1m)), new VoucherLineSpec(b, DrCr.Credit, Money.FromDecimal(1m))])).Code);
        Assert.Equal("voucher.posted_immutable", Assert.Throws<DomainException>(() => voucher.CancelDraft()).Code);
        Assert.Equal("voucher.posted_immutable", Assert.Throws<DomainException>(() => voucher.Post(_company, new VoucherNumber("D1", 2), _user, DateTime.UtcNow)).Code);
    }

    [Fact]
    public void Reversal_mirrors_every_line_and_cannot_itself_be_reversed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var original = Draft(VoucherBaseType.Journal, (a, DrCr.Debit, 60m), (b, DrCr.Debit, 40m), (c, DrCr.Credit, 100m));
        original.Post(_company, new VoucherNumber("D1", 1), _user, DateTime.UtcNow);

        var reversal = Voucher.CreateReversal(Ids.New(), _company, original, Day.AddDays(3), null, _user, null);
        reversal.Post(_company, new VoucherNumber("D1", 2), _user, DateTime.UtcNow);

        Assert.Equal(original.Id, reversal.ReversalOfVoucherId);
        Assert.Equal("Reversal of D1/1", reversal.Narration);
        var net = original.Lines.Concat(reversal.Lines).GroupBy(l => l.LedgerId).ToDictionary(g => g.Key, g => g.Sum(l => l.SignedAmountMinor));
        Assert.All(net.Values, v => Assert.Equal(0, v));
        Assert.Equal("voucher.reverse_reversal", Assert.Throws<DomainException>(() => Voucher.CreateReversal(Ids.New(), _company, reversal, Day.AddDays(4), null, _user, null)).Code);
    }

    [Fact]
    public void Reversal_cannot_predate_original()
    {
        var original = Draft(VoucherBaseType.Journal, (Guid.NewGuid(), DrCr.Debit, 1m), (Guid.NewGuid(), DrCr.Credit, 1m));
        original.Post(_company, new VoucherNumber("D1", 1), _user, DateTime.UtcNow);
        Assert.Equal("voucher.reverse_date", Assert.Throws<DomainException>(() => Voucher.CreateReversal(Ids.New(), _company, original, Day.AddDays(-1), null, _user, null)).Code);
    }

    [Fact]
    public void Voucher_before_books_beginning_is_rejected()
    {
        var ex = Assert.Throws<DomainException>(() => Voucher.CreateDraft(
            Ids.New(), _company, Type(VoucherBaseType.Journal), new DateOnly(2026, 3, 31), new VoucherHeader(),
            [new VoucherLineSpec(Guid.NewGuid(), DrCr.Debit, Money.FromDecimal(1m))], _user, null));
        Assert.Equal("voucher.before_books_beginning", ex.Code);
    }

    [Fact]
    public void Posting_into_a_locked_period_is_rejected()
    {
        _company.SetLockedThrough(Day);
        var voucher = Draft(VoucherBaseType.Journal, (Guid.NewGuid(), DrCr.Debit, 1m), (Guid.NewGuid(), DrCr.Credit, 1m));
        Assert.Equal("voucher.period_locked", Assert.Throws<DomainException>(() => voucher.Post(_company, new VoucherNumber("D1", 1), _user, DateTime.UtcNow)).Code);
    }

    [Fact]
    public void Type_rules_enforce_cash_and_bank_usage()
    {
        var cash = Guid.NewGuid();
        var bank = Guid.NewGuid();
        var expense = Guid.NewGuid();
        var sales = Guid.NewGuid();
        var traits = new Dictionary<Guid, LedgerTraits>
        {
            [cash] = LedgerTraits.CashOrBank,
            [bank] = LedgerTraits.CashOrBank,
            [expense] = LedgerTraits.None,
            [sales] = LedgerTraits.SalesAccount,
        };

        VoucherTypeRules.Validate(Draft(VoucherBaseType.Contra, (bank, DrCr.Debit, 5m), (cash, DrCr.Credit, 5m)), traits);
        VoucherTypeRules.Validate(Draft(VoucherBaseType.Payment, (expense, DrCr.Debit, 5m), (cash, DrCr.Credit, 5m)), traits);
        VoucherTypeRules.Validate(Draft(VoucherBaseType.Receipt, (bank, DrCr.Debit, 5m), (sales, DrCr.Credit, 5m)), traits);
        VoucherTypeRules.Validate(Draft(VoucherBaseType.Sales, (expense, DrCr.Debit, 5m), (sales, DrCr.Credit, 5m)), traits);

        Assert.Equal("voucher.contra_cash_bank_only", Assert.Throws<DomainException>(() =>
            VoucherTypeRules.Validate(Draft(VoucherBaseType.Contra, (expense, DrCr.Debit, 5m), (cash, DrCr.Credit, 5m)), traits)).Code);
        Assert.Equal("voucher.payment_needs_cash_bank", Assert.Throws<DomainException>(() =>
            VoucherTypeRules.Validate(Draft(VoucherBaseType.Payment, (cash, DrCr.Debit, 5m), (expense, DrCr.Credit, 5m)), traits)).Code);
        Assert.Equal("voucher.journal_no_cash_bank", Assert.Throws<DomainException>(() =>
            VoucherTypeRules.Validate(Draft(VoucherBaseType.Journal, (expense, DrCr.Debit, 5m), (cash, DrCr.Credit, 5m)), traits)).Code);
        Assert.Equal("voucher.ledger_not_found", Assert.Throws<DomainException>(() =>
            VoucherTypeRules.Validate(Draft(VoucherBaseType.Journal, (expense, DrCr.Debit, 5m), (Guid.NewGuid(), DrCr.Credit, 5m)), traits)).Code);
    }

    [Fact]
    public void Default_chart_has_a_consistent_hierarchy()
    {
        var companyId = Ids.New();
        var (groups, ledgers, types) = DefaultChartOfAccounts.Build(companyId);
        var hierarchy = new GroupHierarchy(groups);

        Assert.Equal(28, groups.Count);
        Assert.Equal(8, types.Count);
        Assert.Equal(Enum.GetValues<VoucherBaseType>().Order(), types.Select(t => t.BaseType).Order());
        Assert.All(groups.Where(g => !g.IsPrimary), g => Assert.Equal(hierarchy.PrimaryOf(g.Id).Nature, g.Nature));

        var cash = ledgers.Single(l => l.SystemKey == SystemLedgerKeys.Cash);
        Assert.True(hierarchy.TraitsOf(cash.GroupId).HasFlag(LedgerTraits.CashOrBank));
        var pl = ledgers.Single(l => l.SystemKey == SystemLedgerKeys.ProfitAndLoss);
        Assert.Equal(AccountNature.Liability, hierarchy.Get(pl.GroupId).Nature);
    }

    [Fact]
    public void Income_ledger_cannot_carry_an_opening_balance()
    {
        var (groups, _, _) = DefaultChartOfAccounts.Build(_company.Id);
        var sales = groups.Single(g => g.SystemKey == SystemGroupKeys.SalesAccounts);
        var ex = Assert.Throws<DomainException>(() => Ledger.Create(Ids.New(), sales, "Sales", Money.FromDecimal(10m), DrCr.Credit));
        Assert.Equal("ledger.opening_not_allowed", ex.Code);
    }
}
