using TallyNext.Application.Accounts;
using TallyNext.Application.Bills;
using TallyNext.Application.Common;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.UnitTests.Support;

namespace TallyNext.UnitTests.Application;

/// <summary>Bill-wise allocations, defaults, settlements and outstanding reports on SQLite.</summary>
public sealed class BillWiseTests
{
    private static readonly DateOnly AsAt = new(2026, 6, 30);

    private sealed record Setup(LocalTestHost Host, TestBooks Books, Guid Customer, Guid Supplier, Guid Purchases);

    /// <summary>
    /// A bill-wise customer (30 days credit) with opening balance 5,000 Dr: bill OB-1 of 3,000 (due 09-Apr-2026) and
    /// 2,000 on account; a bill-wise supplier (15 days credit).
    /// </summary>
    private static async Task<Setup> SeedAsync()
    {
        var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var groups = await host.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(books.CompanyId));
        Guid Group(string name) => groups.Single(g => g.Name == name).Id;
        async Task<Guid> Ledger(LedgerInput input) => (await host.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(books.CompanyId, input))).Id;

        var customer = await Ledger(new LedgerInput
        {
            Name = "Bill-wise Customer",
            GroupId = Group("Sundry Debtors"),
            IsBillWise = true,
            CreditPeriodDays = 30,
            OpeningBalance = 5_000m,
            OpeningSide = DrCr.Debit,
            OpeningBills = [new OpeningBillInput { Name = "OB-1", Date = new(2026, 3, 10), DueDate = new(2026, 4, 9), Amount = 3_000m, Side = DrCr.Debit }],
        });
        var supplier = await Ledger(new LedgerInput { Name = "Bill-wise Supplier", GroupId = Group("Sundry Creditors"), IsBillWise = true, CreditPeriodDays = 15 });
        var purchases = await Ledger(new LedgerInput { Name = "Purchases (bills)", GroupId = Group("Purchase Accounts") });
        return new Setup(host, books, customer, supplier, purchases);
    }

    private static VoucherInput Voucher(Setup s, VoucherBaseType type, DateOnly date, params (Guid Ledger, DrCr Side, decimal Amount, List<BillAllocationInput>? Bills)[] lines) => new()
    {
        VoucherTypeId = s.Books.Types[type],
        Date = date,
        Post = true,
        Lines = lines.Select(l => new VoucherLineInput { LedgerId = l.Ledger, Side = l.Side, Amount = l.Amount, Bills = l.Bills }).ToList(),
    };

    private static Task<VoucherDto> PostAsync(Setup s, VoucherInput input) =>
        s.Host.RunAsync<VoucherService, VoucherDto>(v => v.CreateAsync(s.Books.CompanyId, input));

    private static Task<OutstandingReport> OutstandingAsync(Setup s, OutstandingKind kind = OutstandingKind.Receivables, DateOnly? asAt = null) =>
        s.Host.RunAsync<BillWiseService, OutstandingReport>(b => b.OutstandingAsync(s.Books.CompanyId, kind, asAt ?? AsAt));

    private static BillAllocationInput Agst(string name, decimal? amount) => new() { Kind = BillAllocationKind.AgstRef, BillName = name, Amount = amount };

    [Fact]
    public async Task A_sale_without_a_breakup_creates_a_bill_named_after_the_voucher_due_after_the_credit_period()
    {
        var s = await SeedAsync();
        await using var _ = s.Host;
        var sale = await PostAsync(s, Voucher(s, VoucherBaseType.Sales, new(2026, 5, 1), (s.Customer, DrCr.Debit, 1_180m, null), (s.Books.Sales, DrCr.Credit, 1_180m, null)));

        var bill = Assert.Single(sale.Lines[0].Bills!);
        Assert.Equal((BillAllocationKind.NewRef, sale.Number, new DateOnly(2026, 5, 31), 1_180m), (bill.Kind, bill.BillName, bill.DueDate, bill.Amount));
        Assert.Null(sale.Lines[1].Bills); // sales ledger is not bill-wise

        var report = await OutstandingAsync(s);
        Assert.Equal(
            [("OB-1", 3_000m, 82, false), (sale.Number!, 1_180m, 30, false)],
            report.Bills.Select(b => (b.BillName, b.Pending, b.OverdueDays, b.IsAdvance)));
        var ledger = Assert.Single(report.Ledgers);
        Assert.Equal((4_180m, 2_000m, 6_180m), (ledger.BillsPending, ledger.OnAccount, ledger.Balance));
        Assert.Equal(1_180m, report.Ageing.Single(a => a.Label == "0-30 days").Amount);
        Assert.Equal(3_000m, report.Ageing.Single(a => a.Label == "61-90 days").Amount);
        Assert.Equal(6_180m, report.Total);
    }

    [Fact]
    public async Task A_receipt_settles_bills_and_leaves_the_rest_on_account()
    {
        var s = await SeedAsync();
        await using var _ = s.Host;
        var sale = await PostAsync(s, Voucher(s, VoucherBaseType.Sales, new(2026, 5, 1), (s.Customer, DrCr.Debit, 1_180m, null), (s.Books.Sales, DrCr.Credit, 1_180m, null)));
        var receipt = await PostAsync(s, Voucher(s, VoucherBaseType.Receipt, new(2026, 6, 10),
            (s.Books.Bank, DrCr.Debit, 2_500m, null),
            (s.Customer, DrCr.Credit, 2_500m, [Agst("ob-1", 1_000m), Agst(sale.Number!, 1_180m), new BillAllocationInput { Kind = BillAllocationKind.OnAccount }])));

        Assert.Equal([1_000m, 1_180m, 320m], receipt.Lines[1].Bills!.Select(b => b.Amount)); // the open amount took the remainder

        var pending = await s.Host.RunAsync<BillWiseService, IReadOnlyList<PendingBillDto>>(b => b.PendingBillsAsync(s.Books.CompanyId, s.Customer));
        Assert.Equal([("OB-1", 2_000m, DrCr.Debit)], pending.Select(p => (p.BillName, p.Amount, p.Side)));

        var ledger = Assert.Single((await OutstandingAsync(s)).Ledgers);
        Assert.Equal((2_000m, 1_680m, 3_680m), (ledger.BillsPending, ledger.OnAccount, ledger.Balance));

        // Reversing the receipt re-opens what it settled.
        await s.Host.RunAsync<VoucherService, VoucherDto>(v => v.ReverseAsync(s.Books.CompanyId, receipt.Id, new ReverseVoucherRequest { Date = new(2026, 6, 20) }));
        var restored = await OutstandingAsync(s);
        Assert.Equal([3_000m, 1_180m], restored.Bills.Select(b => b.Pending));
        Assert.Equal(2_000m, restored.TotalOnAccount);
    }

    [Fact]
    public async Task Advances_show_as_negative_pending_until_adjusted()
    {
        var s = await SeedAsync();
        await using var _ = s.Host;
        await PostAsync(s, Voucher(s, VoucherBaseType.Receipt, new(2026, 6, 12),
            (s.Books.Bank, DrCr.Debit, 500m, null),
            (s.Customer, DrCr.Credit, 500m, [new BillAllocationInput { Kind = BillAllocationKind.Advance, BillName = "ADV-1" }])));
        var advance = (await OutstandingAsync(s)).Bills.Single(b => b.BillName == "ADV-1");
        Assert.Equal((-500m, true), (advance.Pending, advance.IsAdvance));

        var sale = await PostAsync(s, Voucher(s, VoucherBaseType.Sales, new(2026, 6, 15),
            (s.Customer, DrCr.Debit, 1_180m, [Agst("ADV-1", 500m), new BillAllocationInput { Kind = BillAllocationKind.NewRef }]),
            (s.Books.Sales, DrCr.Credit, 1_180m, null)));

        var report = await OutstandingAsync(s);
        Assert.DoesNotContain(report.Bills, b => b.BillName == "ADV-1");
        Assert.Equal(680m, report.Bills.Single(b => b.BillName == sale.Number).Pending);
    }

    [Fact]
    public async Task Unknown_bills_unbalanced_breakups_and_non_bill_wise_ledgers_are_rejected()
    {
        var s = await SeedAsync();
        await using var _ = s.Host;

        var unknown = await Assert.ThrowsAsync<ValidationFailedException>(() => PostAsync(s, Voucher(s, VoucherBaseType.Receipt, new(2026, 6, 1),
            (s.Books.Bank, DrCr.Debit, 100m, null), (s.Customer, DrCr.Credit, 100m, [Agst("NO-SUCH-BILL", 100m)]))));
        Assert.Equal("bill.not_found", unknown.Code);

        var mismatch = await Assert.ThrowsAsync<DomainException>(() => PostAsync(s, Voucher(s, VoucherBaseType.Receipt, new(2026, 6, 1),
            (s.Books.Bank, DrCr.Debit, 100m, null), (s.Customer, DrCr.Credit, 100m, [Agst("OB-1", 60m)]))));
        Assert.Equal("bill.allocation_mismatch", mismatch.Code);

        var notBillWise = await Assert.ThrowsAsync<ValidationFailedException>(() => PostAsync(s, Voucher(s, VoucherBaseType.Receipt, new(2026, 6, 1),
            (s.Books.Bank, DrCr.Debit, 100m, null), (s.Books.Customer, DrCr.Credit, 100m, [Agst("OB-1", 100m)]))));
        Assert.Equal("bill.ledger_not_billwise", notBillWise.Code);

        var groups = await s.Host.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(g => g.ListGroupsAsync(s.Books.CompanyId));
        var openingOnPlainLedger = await Assert.ThrowsAsync<DomainException>(() => s.Host.RunAsync<ChartOfAccountsService, LedgerDto>(c => c.CreateLedgerAsync(s.Books.CompanyId, new LedgerInput
        {
            Name = "Not bill-wise",
            GroupId = groups.Single(g => g.Name == "Sundry Debtors").Id,
            OpeningBills = [new OpeningBillInput { Name = "X", Date = new(2026, 3, 1), Amount = 1m }],
        })));
        Assert.Equal("ledger.opening_bills_not_billwise", openingOnPlainLedger.Code);
    }

    [Fact]
    public async Task Invoices_create_bills_for_the_invoice_total_and_notes_can_settle_them()
    {
        var host = await LocalTestHost.CreateAsync();
        await using var _ = host;
        var gst = await GstBooks.CreateAsync(host);
        var groups = await host.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(c => c.ListGroupsAsync(gst.CompanyId));
        var debtor = (await host.RunAsync<ChartOfAccountsService, LedgerDto>(c => c.CreateLedgerAsync(gst.CompanyId, new LedgerInput
        {
            Name = "Bill-wise Retailer",
            GroupId = groups.Single(g => g.Name == "Sundry Debtors").Id,
            IsBillWise = true,
            CreditPeriodDays = 45,
            StateCode = "27",
        }))).Id;
        var creditor = (await host.RunAsync<ChartOfAccountsService, LedgerDto>(c => c.CreateLedgerAsync(gst.CompanyId, new LedgerInput
        {
            Name = "Bill-wise Wholesaler",
            GroupId = groups.Single(g => g.Name == "Sundry Creditors").Id,
            IsBillWise = true,
            StateCode = "27",
            GstRegistrationType = GstRegistrationType.Regular,
            Gstin = GstBooks.SyntheticGstin("27", "AABCW1234Q"),
        }))).Id;

        var saleRequest = gst.Invoice(VoucherBaseType.Sales, new(2026, 5, 10), debtor, (10, 12.75m));
        saleRequest.RoundOff = true;
        var sale = await gst.PostAsync(host, saleRequest);
        var saleBill = Assert.Single(sale.Lines.Single(l => l.LedgerId == debtor).Bills!);
        Assert.Equal((sale.Number, 150m, new DateOnly(2026, 6, 24)), (saleBill.BillName, saleBill.Amount, saleBill.DueDate)); // the rounded invoice total

        var purchaseRequest = gst.Invoice(VoucherBaseType.Purchase, new(2026, 5, 12), creditor, (5, 100m));
        purchaseRequest.Reference = "SUP-778";
        var purchase = await gst.PostAsync(host, purchaseRequest);
        Assert.Equal("SUP-778", purchase.Lines.Single(l => l.LedgerId == creditor).Bills!.Single().BillName);

        var noteRequest = gst.Invoice(VoucherBaseType.CreditNote, new(2026, 5, 20), debtor, (1, 12.75m));
        noteRequest.PartyBill = new BillAllocationInput { Kind = BillAllocationKind.AgstRef, BillName = sale.Number };
        await gst.PostAsync(host, noteRequest);

        var receivables = await host.RunAsync<BillWiseService, OutstandingReport>(b => b.OutstandingAsync(gst.CompanyId, OutstandingKind.Receivables, AsAt));
        Assert.Equal(150m - 15.05m, receivables.Bills.Single(b => b.BillName == sale.Number).Pending); // 12.75 + 18% = 15.05

        var payables = await host.RunAsync<BillWiseService, OutstandingReport>(b => b.OutstandingAsync(gst.CompanyId, OutstandingKind.Payables, AsAt));
        var bill = Assert.Single(payables.Bills);
        Assert.Equal(("SUP-778", 590m, 590m), (bill.BillName, bill.Original, bill.Pending)); // payables are positive when owed
        Assert.Equal(590m, payables.Total);
    }
}
