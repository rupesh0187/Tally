using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Common;
using TallyNext.Application.Inventory;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;
using TallyNext.UnitTests.Support;

namespace TallyNext.UnitTests.Application;

/// <summary>
/// GST invoices with stock, end to end on SQLite: tax by place of supply, re-derivation on posting, stock valuation
/// and the integration of closing stock into the financial statements.
/// </summary>
public sealed class InvoiceAndInventoryTests
{
    private static readonly DateOnly FyStart = new(2026, 4, 1);
    private static readonly DateOnly FyEnd = new(2027, 3, 31);

    /// <summary>
    /// Opening 10 Widgets @ 100. Purchase 10 @ 120 (intra-State), sell 5 @ 150 locally and 3 @ 200 to a consumer in
    /// Karnataka. Weighted average after the purchase: 2,200 / 20 = 110.
    /// </summary>
    private static async Task<(LocalTestHost Host, GstBooks Gst, VoucherDto Purchase, VoucherDto LocalSale, VoucherDto InterStateSale)> SeedAsync(
        ValuationMethod valuation = ValuationMethod.WeightedAverage)
    {
        var host = await LocalTestHost.CreateAsync();
        var gst = await GstBooks.CreateAsync(host, valuation);
        var purchase = await gst.PostAsync(host, gst.Invoice(VoucherBaseType.Purchase, new(2026, 5, 1), gst.Supplier, (10, 120m)));
        var local = await gst.PostAsync(host, gst.Invoice(VoucherBaseType.Sales, new(2026, 5, 10), gst.LocalCustomer, (5, 150m)));
        var interState = await gst.PostAsync(host, gst.Invoice(VoucherBaseType.Sales, new(2026, 5, 12), gst.OutOfStateCustomer, (3, 200m)));
        return (host, gst, purchase, local, interState);
    }

    [Fact]
    public async Task Invoices_post_balanced_vouchers_with_tax_by_place_of_supply()
    {
        var (host, _, purchase, local, interState) = await SeedAsync();
        await using var _h = host;

        Assert.Equal(VoucherStatus.Posted, local.Status);
        Assert.Equal(SupplyNature.IntraState, local.Invoice!.SupplyNature);
        Assert.Equal((750m, 67.50m, 67.50m, 0m), (local.Invoice.Taxable, local.Invoice.Cgst, local.Invoice.StateTax, local.Invoice.Igst));
        Assert.Equal(885m, local.Total);
        Assert.Equal((885m, 0m), (local.Invoice.GrandTotal, local.Invoice.RoundOff));
        Assert.Equal(local.Lines.Where(l => l.Side == DrCr.Debit).Sum(l => l.Amount), local.Lines.Where(l => l.Side == DrCr.Credit).Sum(l => l.Amount));
        Assert.Equal("Local Customer", local.Lines[0].LedgerName);

        Assert.Equal(SupplyNature.InterState, interState.Invoice!.SupplyNature);
        Assert.Equal("29", interState.Invoice.PlaceOfSupplyStateCode);
        Assert.Equal((600m, 0m, 108m), (interState.Invoice.Taxable, interState.Invoice.Cgst, interState.Invoice.Igst));
        Assert.Contains(interState.Lines, l => l.LedgerName == "IGST" && l.Side == DrCr.Credit && l.Amount == 108m);

        // Inward supply: input tax is debited.
        Assert.Equal(1_416m, purchase.Total);
        Assert.Contains(purchase.Lines, l => l.LedgerName == "CGST" && l.Side == DrCr.Debit && l.Amount == 108m);

        var line = Assert.Single(local.Invoice.Lines);
        Assert.Equal(("Widget", "Main Location", "Nos", 5m, 150m, 18m, "8471"), (line.ItemName, line.GodownName, line.UnitSymbol, line.Quantity, line.Rate, line.GstPercent, line.HsnSac));
    }

    [Fact]
    public async Task Preview_matches_the_invoice_that_is_saved()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var request = gst.Invoice(VoucherBaseType.Sales, new(2026, 6, 1), gst.OutOfStateCustomer, (2, 99.99m), (1, 0.55m));
        request.RoundOff = true;

        var preview = await host.RunAsync<InvoiceService, InvoicePreview>(s => s.PreviewAsync(gst.CompanyId, request));
        var saved = await gst.PostAsync(host, request);

        Assert.Equal(SupplyNature.InterState, preview.SupplyNature);
        Assert.Equal(200.53m, preview.Taxable);
        Assert.Equal(36.10m, preview.Igst); // 35.9964 + 0.099 -> 36.00 + 0.10
        Assert.Equal(237m, preview.GrandTotal);
        Assert.Equal(0.37m, preview.RoundOff);
        Assert.Equal(preview.GrandTotal, saved.Invoice!.GrandTotal);
        Assert.Equal(preview.RoundOff, saved.Invoice.RoundOff);
        Assert.Equal(preview.Igst, saved.Invoice!.Igst);
        Assert.Contains(saved.Lines, l => l.LedgerName == "Round Off" && l.Amount == 0.37m);
    }

    [Fact]
    public async Task Rounding_down_posts_a_debit_round_off_and_the_party_pays_the_rounded_total()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var request = gst.Invoice(VoucherBaseType.Sales, new(2026, 6, 5), gst.LocalCustomer, (10, 12.75m));
        request.RoundOff = true;
        var saved = await gst.PostAsync(host, request);

        // 127.50 + 11.48 + 11.48 = 150.46 -> 150.00
        Assert.Equal((150m, -0.46m), (saved.Invoice!.GrandTotal, saved.Invoice.RoundOff));
        Assert.Contains(saved.Lines, l => l.LedgerName == "Round Off" && l.Side == DrCr.Debit && l.Amount == 0.46m);
        Assert.Contains(saved.Lines, l => l.LedgerId == gst.LocalCustomer && l.Side == DrCr.Debit && l.Amount == 150m);
        Assert.Equal(150.46m, saved.Total); // debit total of the voucher (party + round off)
    }

    [Fact]
    public async Task Stock_summary_values_closing_stock_by_weighted_average()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var summary = await host.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(gst.CompanyId, FyStart, FyEnd));

        var row = Assert.Single(summary.Rows);
        Assert.Equal((10m, 1_000m), (row.OpeningQuantity, row.OpeningValue));
        Assert.Equal((10m, 1_200m), (row.InwardQuantity, row.InwardValue));
        Assert.Equal((8m, 880m), (row.OutwardQuantity, row.OutwardCost)); // 8 x 110
        Assert.Equal((12m, 1_320m), (row.ClosingQuantity, row.ClosingValue));
        Assert.False(row.WentNegative);
        Assert.Equal(12m, Assert.Single(row.ByGodown).Quantity);
    }

    [Fact]
    public async Task Fifo_items_cost_sales_from_the_oldest_lots()
    {
        var (host, gst, _, _, _) = await SeedAsync(ValuationMethod.Fifo);
        await using var _h = host;
        var row = Assert.Single((await host.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(gst.CompanyId, FyStart, FyEnd))).Rows);

        Assert.Equal(800m, row.OutwardCost); // 8 from the opening lot @ 100
        Assert.Equal(1_400m, row.ClosingValue); // 2 @ 100 + 10 @ 120
    }

    [Fact]
    public async Task Closing_stock_flows_into_the_profit_and_loss_and_balance_sheet()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;

        var pl = await host.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(gst.CompanyId, FyStart, FyEnd));
        Assert.False(pl.ExcludesInventory);
        Assert.Equal((1_000m, 1_320m), (pl.OpeningStock, pl.ClosingStock));
        Assert.Equal(1_350m, pl.TradingIncome.Total);
        Assert.Equal(1_200m, pl.DirectCosts.Total);
        Assert.Equal(470m, pl.GrossProfit); // (750 - 550) + (600 - 330)
        Assert.Equal(470m, pl.NetProfit);

        var bs = await host.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(gst.CompanyId, FyEnd));
        Assert.Equal(bs.TotalAssets, bs.TotalLiabilities);
        Assert.Equal(470m, bs.ProfitAndLossBalance);
        Assert.Equal(470m, bs.CurrentPeriodProfit);
        Assert.Equal(1_000m, bs.OpeningDifference); // opening stock was not matched by a capital entry
        Assert.Contains(bs.Assets.Lines, l => l.Name == "Closing stock" && l.Amount == 1_320m);

        var tb = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(gst.CompanyId, FyStart, FyEnd));
        Assert.True(tb.IsBalanced);
        Assert.Equal(1_000m, tb.OpeningDifference);
        Assert.Equal(1_000m, tb.Rows.Single(r => r.Name == "Opening stock").OpeningDebit);

        // Next year: opening stock is last year's closing stock, and last year's profit (incl. the stock gain) is in
        // the Profit & Loss ledger.
        var next = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(gst.CompanyId, new(2027, 4, 1), new(2027, 4, 30)));
        Assert.True(next.IsBalanced);
        Assert.Equal(1_320m, next.Rows.Single(r => r.Name == "Opening stock").OpeningDebit);
        Assert.Equal(470m, next.Rows.Single(r => r.Name == "Profit & Loss A/c" && !r.IsGroup).OpeningCredit);

        var nextPl = await host.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(gst.CompanyId, new(2027, 4, 1), new(2027, 4, 30)));
        Assert.Equal((1_320m, 1_320m, 0m), (nextPl.OpeningStock, nextPl.ClosingStock, nextPl.GrossProfit));
    }

    [Fact]
    public async Task Gst_summary_matches_the_tax_ledger_movements()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var summary = await host.RunAsync<ReportService, GstSummaryReport>(s => s.GstSummaryAsync(gst.CompanyId, FyStart, FyEnd));

        Assert.Equal(2, summary.Outward.Count);
        var intra = summary.Outward.Single(r => r.SupplyNature == SupplyNature.IntraState);
        Assert.Equal((18m, 750m, 67.50m, 67.50m), (intra.GstPercent, intra.Taxable, intra.Cgst, intra.StateTax));
        Assert.Equal((600m, 108m), (summary.Outward.Single(r => r.SupplyNature == SupplyNature.InterState).Taxable, summary.Outward.Single(r => r.SupplyNature == SupplyNature.InterState).Igst));
        Assert.Equal(1_200m, Assert.Single(summary.Inward).Taxable);
        Assert.Equal((243m, 216m, 27m), (summary.OutputTax, summary.InputTax, summary.NetPayable));

        // Cross-check against the books: the net credit on the GST duty ledgers is the net tax payable.
        var ledgerNet = await host.QueryAsync(async db =>
        {
            var taxLedgers = await db.Ledgers.Where(l => l.CompanyId == gst.CompanyId && l.GstTaxType != GstTaxType.None).Select(l => l.Id).ToListAsync();
            var lines = await db.VoucherLines.Where(l => l.IsPosted && taxLedgers.Contains(l.LedgerId)).Select(l => new { l.Side, l.AmountMinor }).ToListAsync();
            return lines.Sum(l => l.Side == DrCr.Credit ? l.AmountMinor : -l.AmountMinor);
        });
        Assert.Equal(summary.NetPayable, ledgerNet / 100m);
    }

    [Fact]
    public async Task Reversing_an_invoice_restores_stock_and_nets_its_gst_to_zero()
    {
        var (host, gst, _, _, interState) = await SeedAsync();
        await using var _h = host;
        var reversal = await host.RunAsync<VoucherService, VoucherDto>(s => s.ReverseAsync(gst.CompanyId, interState.Id, new ReverseVoucherRequest { Date = new(2026, 5, 20) }));
        Assert.NotNull(reversal.Invoice);
        Assert.All(reversal.Invoice!.Lines, l => Assert.Equal(StockDirection.In, l.Direction));

        var row = Assert.Single((await host.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(gst.CompanyId, FyStart, FyEnd))).Rows);
        Assert.Equal(15m, row.ClosingQuantity);
        Assert.Equal(1_650m, row.ClosingValue); // back at cost (110)

        var summary = await host.RunAsync<ReportService, GstSummaryReport>(s => s.GstSummaryAsync(gst.CompanyId, FyStart, FyEnd));
        Assert.DoesNotContain(summary.Outward, r => r.SupplyNature == SupplyNature.InterState);
        Assert.Equal(135m - 216m, summary.NetPayable);
    }

    [Fact]
    public async Task Credit_note_brings_goods_back_at_cost_and_reduces_output_tax()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var note = await gst.PostAsync(host, gst.Invoice(VoucherBaseType.CreditNote, new(2026, 5, 15), gst.LocalCustomer, (1, 150m)));
        Assert.Equal(177m, note.Total);
        Assert.Equal(DrCr.Credit, note.Lines[0].Side); // the customer is credited

        var row = Assert.Single((await host.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(gst.CompanyId, FyStart, FyEnd))).Rows);
        Assert.Equal(13m, row.ClosingQuantity);
        Assert.Equal(1_430m, row.ClosingValue);

        var summary = await host.RunAsync<ReportService, GstSummaryReport>(s => s.GstSummaryAsync(gst.CompanyId, FyStart, FyEnd));
        Assert.Equal(600m, summary.Outward.Single(r => r.SupplyNature == SupplyNature.IntraState).Taxable);
    }

    [Fact]
    public async Task A_tampered_invoice_is_rejected_when_posted()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var request = gst.Invoice(VoucherBaseType.Sales, new(2026, 6, 2), gst.LocalCustomer, (1, 100m));
        request.Post = false;
        var draft = await gst.PostAsync(host, request);
        Assert.Equal(VoucherStatus.Draft, draft.Status);

        // Lower the stored CGST behind the application's back (a buggy or malicious client).
        await host.QueryAsync(db => db.VoucherItemLines.Where(l => l.VoucherId == draft.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.CgstMinor, l => l.CgstMinor - 100)));

        var ex = await Assert.ThrowsAsync<DomainException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.PostAsync(gst.CompanyId, draft.Id)));
        Assert.Equal("invoice.tax_mismatch", ex.Code);
    }

    [Fact]
    public async Task Missing_gst_rate_is_reported_when_tax_applies()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var units = await host.RunAsync<InventoryService, IReadOnlyList<UnitDto>>(s => s.ListUnitsAsync(gst.CompanyId));
        var gadget = await host.RunAsync<InventoryService, StockItemDto>(s => s.SaveItemAsync(gst.CompanyId, null, new StockItemInput
        {
            Name = "Gadget without rate",
            UnitId = units.Single(u => u.Symbol == "Nos").Id,
        }));
        var request = gst.Invoice(VoucherBaseType.Sales, new(2026, 6, 4), gst.LocalCustomer, (1, 100m));
        request.Lines[0].StockItemId = gadget.Id;

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => gst.PostAsync(host, request));
        Assert.Equal("gst.rate_missing", ex.Code);
    }

    [Fact]
    public async Task Accounting_edits_of_an_invoice_draft_are_refused()
    {
        var (host, gst, _, _, _) = await SeedAsync();
        await using var _h = host;
        var request = gst.Invoice(VoucherBaseType.Sales, new(2026, 6, 3), gst.LocalCustomer, (1, 100m));
        request.Post = false;
        var draft = await gst.PostAsync(host, request);

        var edit = gst.Books.Voucher(VoucherBaseType.Sales, new(2026, 6, 3), (gst.LocalCustomer, DrCr.Debit, 1m), (gst.Books.Sales, DrCr.Credit, 1m));
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.UpdateDraftAsync(gst.CompanyId, draft.Id, edit)));
        Assert.Equal("voucher.is_invoice", ex.Code);
    }

    [Fact]
    public async Task Default_inventory_masters_are_created_for_older_companies_only_once()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        Assert.False(await host.RunAsync<InventoryService, bool>(s => s.EnsureDefaultsAsync(books.CompanyId)));

        // Simulate a company created before inventory existed.
        await host.QueryAsync(db => db.Units.Where(u => u.CompanyId == books.CompanyId).ExecuteDeleteAsync());
        await host.QueryAsync(db => db.Godowns.Where(g => g.CompanyId == books.CompanyId).ExecuteDeleteAsync());

        Assert.True(await host.RunAsync<InventoryService, bool>(s => s.EnsureDefaultsAsync(books.CompanyId)));
        Assert.False(await host.RunAsync<InventoryService, bool>(s => s.EnsureDefaultsAsync(books.CompanyId)));
        Assert.Equal("Nos", Assert.Single(await host.RunAsync<InventoryService, IReadOnlyList<UnitDto>>(s => s.ListUnitsAsync(books.CompanyId))).Symbol);
        var main = Assert.Single(await host.RunAsync<InventoryService, IReadOnlyList<GodownDto>>(s => s.ListGodownsAsync(books.CompanyId)));
        Assert.True(main.IsSystem);
    }
}
