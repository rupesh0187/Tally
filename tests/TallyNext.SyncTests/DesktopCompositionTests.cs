using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Companies;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Vouchers;
using TallyNext.Desktop.Core;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.SyncTests.Support;
using TallyNext.UI.Services;

namespace TallyNext.SyncTests;

/// <summary>
/// Composes the desktop exactly as the MAUI shell does (minus the WebView) and drives it through the same
/// interfaces the shared UI uses.
/// </summary>
public sealed class DesktopCompositionTests
{
    private const string Owner = "owner@desktop.test";

    [Fact]
    public async Task Desktop_composition_signs_in_works_offline_and_synchronizes_through_the_ui_interfaces()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        var file = Path.Combine(Path.GetTempPath(), $"tallynext-desktop-{Guid.NewGuid():N}.db");
        var network = new NetworkSwitch();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTallyNextDesktop(o => o.DatabasePath = file, _ => server.CreateDefaultClient(network));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            await provider.GetRequiredService<DesktopStartup>().InitializeAsync();
            var signIn = provider.GetRequiredService<IDesktopSignIn>();
            var state = provider.GetRequiredService<AppState>();
            var runner = provider.GetRequiredService<IUseCaseRunner>();
            var sync = provider.GetRequiredService<ISyncPanel>();
            var host = provider.GetRequiredService<IHostFeatures>();
            Assert.True(host.IsDesktop);
            Assert.False(signIn.IsSignedIn);
            await Assert.ThrowsAsync<TallyNext.Application.Common.UnauthenticatedException>(() =>
                runner.RunAsync<CompanyService, IReadOnlyList<CompanySummaryDto>>(s => s.ListMineAsync()));

            var result = await signIn.SignInAsync(Owner, TestUsers.Password, "http://localhost");
            Assert.True(result.Succeeded, result.Error);
            Assert.False(result.Offline);
            Assert.Equal("http://localhost", signIn.DefaultServerUrl);

            var company = await runner.RunAsync<CompanyService, CompanyDto>(s => s.CreateAsync(new CreateCompanyRequest
            {
                Name = "Desktop Shell Co (Synthetic)",
                BooksBeginningDate = new DateOnly(2026, 4, 1),
            }));
            var summary = (await runner.RunAsync<CompanyService, IReadOnlyList<CompanySummaryDto>>(s => s.ListMineAsync())).Single();
            state.SelectCompany(summary);

            // Work offline through the same calls the voucher screen makes.
            network.Mode = NetworkMode.Offline;
            var ledgers = await provider.GetRequiredService<LedgerCache>().GetAsync(company.Id);
            var types = await runner.RunAsync<VoucherService, IReadOnlyList<VoucherTypeDto>>(s => s.ListTypesAsync(company.Id));
            var groups = await runner.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService, IReadOnlyList<TallyNext.Contracts.Accounts.AccountGroupDto>>(s => s.ListGroupsAsync(company.Id));
            var expense = await runner.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService, TallyNext.Contracts.Accounts.LedgerDto>(s => s.CreateLedgerAsync(company.Id,
                new TallyNext.Contracts.Accounts.LedgerInput { Name = "Stationery", GroupId = groups.Single(g => g.Name == "Indirect Expenses").Id }));
            var cash = ledgers.Single(l => l.Name == "Cash").Id;
            var voucher = await runner.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(company.Id, new VoucherInput
            {
                VoucherTypeId = types.Single(t => t.BaseType == VoucherBaseType.Payment).Id,
                Date = new DateOnly(2026, 8, 1),
                Lines =
                [
                    new VoucherLineInput { LedgerId = expense.Id, Side = DrCr.Debit, Amount = 345m },
                    new VoucherLineInput { LedgerId = cash, Side = DrCr.Credit, Amount = 345m },
                ],
            }));
            Assert.Equal(VoucherStatus.Posted, voucher.Status);
            await sync.RefreshAsync();
            Assert.True(sync.Status.PendingCount > 0);
            await Assert.ThrowsAsync<SyncUnavailableException>(() => sync.SyncNowAsync());
            Assert.Equal(SyncPanelState.Offline, sync.Status.State);
            var unsynced = await sync.UnsyncedAsync(company.Id);
            Assert.Contains(unsynced, u => u.EntityId == voucher.Id);

            network.Mode = NetworkMode.Online;
            await sync.SyncNowAsync();
            Assert.Equal(SyncPanelState.UpToDate, sync.Status.State);
            Assert.Equal(0, sync.Status.PendingCount);
            Assert.NotNull(sync.Status.LastSuccessUtc);
            Assert.Empty(await sync.IssuesAsync(company.Id));

            await host.SignOutAsync();
            Assert.False(signIn.IsSignedIn);
            Assert.False(state.HasCompany);
        }
        finally
        {
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(file + suffix);
            }
        }
    }
}
