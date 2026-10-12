using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Reports;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Common;
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
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    private static string Json<T>(T value) => SyncJson.Serialize(value!);
}
