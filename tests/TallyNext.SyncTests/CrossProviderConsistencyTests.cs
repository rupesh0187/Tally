using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Inventory;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Common;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

/// <summary>
/// The server database (PostgreSQL when TALLYNEXT_TEST_POSTGRES is set) and the device database (SQLite) must
/// compute identical reports from identical data.
/// </summary>
public sealed class CrossProviderConsistencyTests(ITestOutputHelper output)
{
    private const string Owner = "owner@consistency.test";

    [Fact]
    public async Task Server_and_device_compute_identical_reports()
    {
        await using var server = new ApiServer();
        output.WriteLine($"Server provider: {server.Provider}");
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);

        var amounts = new[] { 0.01m, 0.99m, 1_234.56m, 99_999.99m, 12_345_678.90m, 0.05m, 7.77m };
        var day = new DateOnly(2026, 4, 2);
        foreach (var amount in amounts)
        {
            await books.PostPaymentAsync(device, amount, day);
            day = day.AddDays(37);
        }

        await device.RunAsync<TallyNext.Application.Vouchers.VoucherService, TallyNext.Contracts.Vouchers.VoucherDto>(s => s.CreateAsync(books.CompanyId, new TallyNext.Contracts.Vouchers.VoucherInput
        {
            VoucherTypeId = books.Types[VoucherBaseType.Receipt],
            Date = new DateOnly(2027, 4, 15),
            Post = true,
            Lines =
            [
                new TallyNext.Contracts.Vouchers.VoucherLineInput { LedgerId = books.Bank, Side = DrCr.Debit, Amount = 500.5m },
                new TallyNext.Contracts.Vouchers.VoucherLineInput { LedgerId = books.Customer, Side = DrCr.Credit, Amount = 500.5m },
            ],
        }));
        await device.SyncAsync();

        var api = await server.SignedInClientAsync(Owner);
        async Task<T> Server<T>(string path) => (await api.GetFromJsonAsync<T>($"/api/companies/{books.CompanyId}/reports/{path}", SyncJson.Options))!;

        foreach (var (from, to) in new[] { (new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)), (new DateOnly(2026, 9, 1), new DateOnly(2027, 6, 30)) })
        {
            var serverTb = await Server<TrialBalanceReport>($"trial-balance?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
            var deviceTb = await device.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, from, to));
            Assert.Equal(Json(deviceTb), Json(serverTb));
            Assert.True(serverTb.IsBalanced);

            var serverPl = await Server<ProfitAndLossReport>($"profit-and-loss?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
            var devicePl = await device.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(books.CompanyId, from, to));
            Assert.Equal(Json(devicePl), Json(serverPl));

            var serverLedger = await Server<LedgerReport>($"ledger/{books.Bank}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&pageSize=3&page=2");
            var deviceLedger = await device.RunAsync<ReportService, LedgerReport>(s => s.LedgerAsync(books.CompanyId, books.Bank, from, to, 2, 3));
            Assert.Equal(Json(deviceLedger), Json(serverLedger));
        }

        var serverBs = await Server<BalanceSheetReport>("balance-sheet?asAt=2027-04-30");
        var deviceBs = await device.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(books.CompanyId, new DateOnly(2027, 4, 30)));
        Assert.Equal(Json(deviceBs), Json(serverBs));
        Assert.Equal(serverBs.TotalAssets, serverBs.TotalLiabilities);
        Assert.Equal(amounts.Sum(), -serverBs.ProfitAndLossBalance);
    }

    [Fact]
    public async Task Server_and_device_compute_identical_stock_gst_and_statement_reports()
    {
        await using var server = new ApiServer();
        output.WriteLine($"Server provider: {server.Provider}");
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var gst = await DeviceGstBooks.CreateAsync(device);
        var id = gst.CompanyId;

        // A FIFO item in kilograms (three decimals) whose rate changes mid-year.
        var kg = await device.RunAsync<InventoryService, UnitDto>(s => s.SaveUnitAsync(id, null, new UnitInput { Symbol = "Kg", Name = "Kilogram", DecimalPlaces = 3 }));
        var powder = (await device.RunAsync<InventoryService, StockItemDto>(s => s.SaveItemAsync(id, null, new StockItemInput
        {
            Name = "Sync Powder",
            UnitId = kg.Id,
            ValuationMethod = ValuationMethod.Fifo,
            GstRates =
            [
                new GstRateInput { EffectiveFrom = DeviceGstBooks.FyStart, IgstPercent = 5m, HsnSac = "3824" },
                new GstRateInput { EffectiveFrom = new DateOnly(2026, 10, 1), IgstPercent = 12m, HsnSac = "3824" },
            ],
        }))).Id;

        Task<TallyNext.Contracts.Vouchers.VoucherDto> Post(VoucherBaseType type, Guid party, DateOnly date, params (Guid Item, decimal Quantity, decimal Rate)[] lines) =>
            gst.PostAsync(device, gst.Invoice(type, party, date, lines));

        await Post(VoucherBaseType.Purchase, gst.Supplier, new(2026, 4, 10), (gst.Widget, 7, 213.37m));
        await Post(VoucherBaseType.Purchase, gst.Supplier, new(2026, 5, 3), (powder, 12.345m, 98.7654m));
        await Post(VoucherBaseType.DebitNote, gst.Supplier, new(2026, 5, 4), (gst.Widget, 1, 213.37m));
        await Post(VoucherBaseType.Sales, gst.LocalCustomer, new(2026, 4, 20), (gst.Widget, 4, 333.33m));
        await Post(VoucherBaseType.Sales, gst.OutOfStateCustomer, new(2026, 6, 1), (powder, 10.001m, 150m), (gst.Widget, 1, 301.01m));
        await Post(VoucherBaseType.CreditNote, gst.LocalCustomer, new(2026, 6, 15), (powder, 0.5m, 150m));
        await Post(VoucherBaseType.Purchase, gst.Supplier, new(2026, 11, 2), (powder, 3.5m, 101.01m));
        await Post(VoucherBaseType.Sales, gst.LocalCustomer, new(2026, 12, 1), (powder, 2.25m, 160m));
        var rounded = gst.Invoice(VoucherBaseType.Sales, gst.OutOfStateCustomer, new(2026, 12, 5), (gst.Widget, 2, 299.99m));
        rounded.RoundOff = true;
        var toReverse = await gst.PostAsync(device, rounded);
        await device.RunAsync<VoucherService, TallyNext.Contracts.Vouchers.VoucherDto>(s => s.ReverseAsync(id, toReverse.Id, new TallyNext.Contracts.Vouchers.ReverseVoucherRequest { Date = new DateOnly(2026, 12, 15) }));
        await Post(VoucherBaseType.Sales, gst.LocalCustomer, new(2027, 4, 2), (gst.Widget, 1, 350m));
        var report = await device.SyncAsync();
        Assert.Equal((0, 0), (report.Conflicts, report.Rejected));

        var api = await server.SignedInClientAsync(Owner);
        async Task<T> Server<T>(string path) => (await api.GetFromJsonAsync<T>($"/api/companies/{id}/reports/{path}", SyncJson.Options))!;

        foreach (var (from, to) in new[] { (DeviceGstBooks.FyStart, DeviceGstBooks.FyEnd), (new DateOnly(2026, 5, 1), new DateOnly(2026, 12, 31)), (new DateOnly(2026, 10, 1), new DateOnly(2027, 4, 30)) })
        {
            var range = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
            Assert.Equal(Json(await device.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(id, from, to))), Json(await Server<StockSummaryReport>($"stock-summary?{range}")));
            Assert.Equal(Json(await device.RunAsync<ReportService, GstSummaryReport>(s => s.GstSummaryAsync(id, from, to))), Json(await Server<GstSummaryReport>($"gst-summary?{range}")));
            Assert.Equal(Json(await device.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(id, from, to))), Json(await Server<ProfitAndLossReport>($"profit-and-loss?{range}")));
            var serverTb = await Server<TrialBalanceReport>($"trial-balance?{range}");
            Assert.Equal(Json(await device.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(id, from, to))), Json(serverTb));
            Assert.True(serverTb.IsBalanced);
        }

        foreach (var asAt in new[] { new DateOnly(2026, 6, 30), new DateOnly(2027, 3, 31), new DateOnly(2027, 4, 30) })
        {
            var serverBs = await Server<BalanceSheetReport>($"balance-sheet?asAt={asAt:yyyy-MM-dd}");
            Assert.Equal(Json(await device.RunAsync<ReportService, BalanceSheetReport>(s => s.BalanceSheetAsync(id, asAt))), Json(serverBs));
            Assert.Equal(serverBs.TotalAssets, serverBs.TotalLiabilities);
        }

        var stock = await Server<StockSummaryReport>($"stock-summary?from={DeviceGstBooks.FyStart:yyyy-MM-dd}&to={DeviceGstBooks.FyEnd:yyyy-MM-dd}");
        var powderRow = stock.Rows.Single(r => r.Name == "Sync Powder");
        Assert.Equal(12.345m - 10.001m + 0.5m + 3.5m - 2.25m, powderRow.ClosingQuantity);
        var widgetRow = stock.Rows.Single(r => r.Name == "Sync Widget");
        Assert.Equal(5m + 7m - 1m - 4m - 1m, widgetRow.ClosingQuantity); // the reversed sale is back in stock
    }

    [Fact]
    public async Task PostgreSQL_schema_is_created_by_the_migration()
    {
        await using var server = new ApiServer();
        if (server.Provider != "Postgres")
        {
            Assert.Skip("Set TALLYNEXT_TEST_POSTGRES to run PostgreSQL-specific tests.");
        }

        _ = server.CreateClient();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("_InitialServer", StringComparison.Ordinal));
        Assert.Contains(applied, m => m.EndsWith("_InventoryAndGst", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    private static string Json<T>(T value) => SyncJson.Serialize(value!);
}
