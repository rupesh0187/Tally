using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.UnitTests.Support;

namespace TallyNext.UnitTests.Application;

/// <summary>Report totals must reconcile with the underlying postings and with each other.</summary>
public sealed class ReportReconciliationTests
{
    private static async Task<(LocalTestHost Host, TestBooks Books)> SeedAsync()
    {
        var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        async Task Post(VoucherInput input) => await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));

        // FY 2026-27
        await Post(books.Voucher(VoucherBaseType.Sales, new(2026, 4, 5), (books.Customer, DrCr.Debit, 118_000m), (books.Sales, DrCr.Credit, 118_000m)));
        await Post(books.Voucher(VoucherBaseType.Receipt, new(2026, 4, 20), (books.Bank, DrCr.Debit, 100_000m), (books.Customer, DrCr.Credit, 100_000m)));
        await Post(books.Voucher(VoucherBaseType.Payment, new(2026, 5, 1), (books.Rent, DrCr.Debit, 25_000m), (books.Bank, DrCr.Credit, 25_000m)));
        await Post(books.Voucher(VoucherBaseType.Contra, new(2026, 5, 2), (books.Cash, DrCr.Debit, 5_000m), (books.Bank, DrCr.Credit, 5_000m)));
        await Post(books.Voucher(VoucherBaseType.Payment, new(2026, 6, 15), (books.Rent, DrCr.Debit, 1_234.56m), (books.Cash, DrCr.Credit, 1_234.56m)));

        // FY 2027-28 (income/expense must restart from zero; prior profit carries into Profit & Loss A/c)
        await Post(books.Voucher(VoucherBaseType.Sales, new(2027, 4, 10), (books.Customer, DrCr.Debit, 10_000m), (books.Sales, DrCr.Credit, 10_000m)));
        await Post(books.Voucher(VoucherBaseType.Payment, new(2027, 4, 11), (books.Rent, DrCr.Debit, 2_000m), (books.Cash, DrCr.Credit, 2_000m)));
        return (host, books);
    }

    [Fact]
    public async Task Trial_balance_balances_and_period_totals_match_raw_postings()
    {
        var (host, books) = await SeedAsync();
        await using var _ = host;
        var tb = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, new(2026, 4, 1), new(2027, 3, 31)));

        Assert.True(tb.IsBalanced);
        Assert.Equal(0m, tb.OpeningDifference);
        Assert.Equal(tb.TotalClosingDebit, tb.TotalClosingCredit);
        Assert.Equal(tb.TotalPeriodDebit, tb.TotalPeriodCredit);

        var rawDebit = await host.QueryAsync(db => db.VoucherLines
            .Where(l => l.CompanyId == books.CompanyId && l.IsPosted && l.VoucherDate <= new DateOnly(2027, 3, 31) && l.Side == DrCr.Debit)
            .SumAsync(l => l.AmountMinor));
        Assert.Equal(rawDebit / 100m, tb.TotalPeriodDebit);
        Assert.Equal(118_000m + 100_000m + 25_000m + 5_000m + 1_234.56m, tb.TotalPeriodDebit);

        var bank = tb.Rows.Single(r => r.Id == books.Bank);
        Assert.Equal(50_000m, bank.OpeningDebit);
        Assert.Equal(120_000m, bank.ClosingDebit); // 50,000 + 100,000 - 25,000 - 5,000
    }

    [Fact]
    public async Task Income_and_expense_restart_each_financial_year_and_carry_into_profit_and_loss()
    {
        var (host, books) = await SeedAsync();
        await using var _ = host;
        var tb = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, new(2027, 4, 1), new(2027, 4, 30)));
        Assert.True(tb.IsBalanced);

        var rent = tb.Rows.Single(r => r.Id == books.Rent);
        Assert.Equal(0m, rent.OpeningDebit);
        Assert.Equal(2_000m, rent.ClosingDebit);

        var pl = tb.Rows.Single(r => r.Name == "Profit & Loss A/c" && !r.IsGroup);
        Assert.Equal(118_000m - 26_234.56m, pl.OpeningCredit);
    }

    [Fact]
    public async Task Profit_and_loss_and_balance_sheet_agree_and_balance()
    {
        var (host, books) = await SeedAsync();
        await using var _ = host;
        var pl = await host.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(books.CompanyId, new(2026, 4, 1), new(2027, 3, 31)));
        Assert.Equal(118_000m, pl.TradingIncome.Total);
        Assert.Equal(0m, pl.DirectCosts.Total);
        Assert.Equal(118_000m, pl.GrossProfit);
        Assert.Equal(26_234.56m, pl.IndirectExpenses.Total);
        Assert.Equal(91_765.44m, pl.NetProfit);

        var bs = await host.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(books.CompanyId, new(2027, 3, 31)));
        Assert.Equal(bs.TotalAssets, bs.TotalLiabilities);
        Assert.Equal(pl.NetProfit, bs.ProfitAndLossBalance);
        Assert.Equal(pl.NetProfit, bs.CurrentPeriodProfit);
        Assert.Equal(0m, bs.OpeningDifference);

        // Next year: last year's profit becomes the opening of the P&L account, no double counting.
        var bs2 = await host.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(books.CompanyId, new(2027, 4, 30)));
        Assert.Equal(bs2.TotalAssets, bs2.TotalLiabilities);
        Assert.Equal(8_000m, bs2.CurrentPeriodProfit);
        Assert.Equal(91_765.44m + 8_000m, bs2.ProfitAndLossBalance);
        var plLines = bs2.Liabilities.Lines.SkipWhile(l => l.Name != "Profit & Loss A/c").Take(3).ToList();
        Assert.Equal(91_765.44m, plLines[1].Amount);
    }

    [Fact]
    public async Task Ledger_report_running_balance_is_consistent_across_pages()
    {
        var (host, books) = await SeedAsync();
        await using var _ = host;
        var full = await host.RunAsync<ReportService, LedgerReport>(s => s.LedgerAsync(books.CompanyId, books.Bank, new(2026, 4, 1), new(2027, 3, 31), 1, 50));
        var page2 = await host.RunAsync<ReportService, LedgerReport>(s => s.LedgerAsync(books.CompanyId, books.Bank, new(2026, 4, 1), new(2027, 3, 31), 2, 1));

        Assert.Equal(3, full.Rows.TotalCount);
        Assert.Equal(full.Rows.Items[1].BalanceDebit, page2.Rows.Items.Single().BalanceDebit);
        Assert.Equal(full.Rows.Items[^1].BalanceDebit, full.ClosingDebit);
        Assert.Equal(full.OpeningDebit + full.PeriodDebit - full.PeriodCredit, full.ClosingDebit);
        Assert.Equal("Customer One", full.Rows.Items[0].Particulars);
    }

    [Fact]
    public async Task Day_book_lists_posted_vouchers_in_date_order()
    {
        var (host, books) = await SeedAsync();
        await using var _ = host;
        var dayBook = await host.RunAsync<ReportService, DayBookReport>(s => s.DayBookAsync(books.CompanyId, new(2026, 4, 1), new(2026, 5, 31)));
        Assert.Equal(4, dayBook.Rows.TotalCount);
        Assert.Equal(new DateOnly(2026, 4, 5), dayBook.Rows.Items[0].Date);
        Assert.Equal(118_000m + 100_000m + 25_000m + 5_000m, dayBook.PeriodTotal);
        Assert.True(dayBook.Rows.Items.SequenceEqual(dayBook.Rows.Items.OrderBy(r => r.Date)));
    }

    [Fact]
    public async Task Opening_balance_difference_is_reported_not_hidden()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        await host.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService, TallyNext.Contracts.Accounts.LedgerDto>(async s =>
        {
            var ledger = await s.GetLedgerAsync(books.CompanyId, books.Cash);
            return await s.UpdateLedgerAsync(books.CompanyId, books.Cash, new TallyNext.Contracts.Accounts.LedgerInput
            {
                Name = ledger.Name,
                GroupId = ledger.GroupId,
                OpeningBalance = 750m,
                OpeningSide = DrCr.Debit,
            });
        });

        var tb = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, new(2026, 4, 1), new(2026, 4, 30)));
        Assert.Equal(750m, tb.OpeningDifference);
        Assert.True(tb.IsBalanced);
        var bs = await host.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(books.CompanyId, new(2026, 4, 30)));
        Assert.Equal(bs.TotalAssets, bs.TotalLiabilities);
        Assert.Contains(bs.Liabilities.Lines, l => l.Name == "Difference in opening balances" && l.Amount == 750m);
    }
}
