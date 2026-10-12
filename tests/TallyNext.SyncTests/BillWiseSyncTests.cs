using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Accounts;
using TallyNext.Application.Bills;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

/// <summary>Bill-wise breakups made offline replicate exactly; the server re-checks their arithmetic.</summary>
public sealed class BillWiseSyncTests
{
    private const string Owner = "owner@billwise-sync.test";
    private static readonly DateOnly AsAt = new(2026, 9, 30);

    private sealed record Setup(DeviceBooks Books, Guid Customer, Guid Sales);

    private static async Task<Setup> CreateAsync(DeviceHost device)
    {
        var books = await DeviceBooks.CreateAsync(device);
        var groups = await device.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(books.CompanyId));
        Guid Group(string name) => groups.Single(g => g.Name == name).Id;
        var customer = await device.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(books.CompanyId, new LedgerInput
        {
            Name = "Sync Bill Customer",
            GroupId = Group("Sundry Debtors"),
            IsBillWise = true,
            CreditPeriodDays = 30,
            OpeningBalance = 2_000m,
            OpeningSide = DrCr.Debit,
            OpeningBills = [new OpeningBillInput { Name = "OPEN-7", Date = new(2026, 3, 15), DueDate = new(2026, 4, 14), Amount = 2_000m }],
        }));
        var sales = await device.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(books.CompanyId, new LedgerInput { Name = "Sync Bill Sales", GroupId = Group("Sales Accounts") }));
        return new Setup(books, customer.Id, sales.Id);
    }

    private static Task<VoucherDto> SaleAsync(DeviceHost device, Setup s, decimal amount, DateOnly date) =>
        device.RunAsync<VoucherService, VoucherDto>(v => v.CreateAsync(s.Books.CompanyId, new VoucherInput
        {
            VoucherTypeId = s.Books.Types[VoucherBaseType.Sales],
            Date = date,
            Lines =
            [
                new VoucherLineInput { LedgerId = s.Customer, Side = DrCr.Debit, Amount = amount },
                new VoucherLineInput { LedgerId = s.Sales, Side = DrCr.Credit, Amount = amount },
            ],
        }));

    private static Task<VoucherDto> ReceiptAsync(DeviceHost device, Setup s, decimal amount, DateOnly date, params BillAllocationInput[] bills) =>
        device.RunAsync<VoucherService, VoucherDto>(v => v.CreateAsync(s.Books.CompanyId, new VoucherInput
        {
            VoucherTypeId = s.Books.Types[VoucherBaseType.Receipt],
            Date = date,
            Lines =
            [
                new VoucherLineInput { LedgerId = s.Books.Bank, Side = DrCr.Debit, Amount = amount },
                new VoucherLineInput { LedgerId = s.Customer, Side = DrCr.Credit, Amount = amount, Bills = [.. bills] },
            ],
        }));

    private static BillAllocationInput Agst(string name, decimal? amount = null) => new() { Kind = BillAllocationKind.AgstRef, BillName = name, Amount = amount };

    [Fact]
    public async Task Bills_made_offline_on_two_devices_meet_on_the_server()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device1 = await DeviceHost.StartAsync(server);
        await device1.SignInAsync(Owner);

        device1.Network.Mode = NetworkMode.Offline;
        var s = await CreateAsync(device1);
        var id = s.Books.CompanyId;
        var sale = await SaleAsync(device1, s, 1_500m, new(2026, 6, 1));
        await ReceiptAsync(device1, s, 2_500m, new(2026, 6, 20), Agst("OPEN-7", 2_000m), Agst(sale.Number!));
        device1.Network.Mode = NetworkMode.Online;
        var first = await device1.SyncAsync();
        Assert.Equal((0, 0), (first.Conflicts, first.Rejected));

        // A second device settles the rest of the same bill offline, from what it downloaded.
        await using var device2 = await DeviceHost.StartAsync(server);
        await device2.SignInAsync(Owner);
        await device2.SyncAsync();
        var pendingOn2 = await device2.RunAsync<BillWiseService, IReadOnlyList<PendingBillDto>>(b => b.PendingBillsAsync(id, s.Customer));
        Assert.Equal([(sale.Number!, 1_000m)], pendingOn2.Select(p => (p.BillName, p.Amount)));
        device2.Network.Mode = NetworkMode.Offline;
        var s2 = s with { Books = await DeviceBooks.OpenAsync(device2, id) };
        await ReceiptAsync(device2, s2, 1_000m, new(2026, 7, 5), Agst(sale.Number!));
        device2.Network.Mode = NetworkMode.Online;
        Assert.Equal(0, (await device2.SyncAsync()).Rejected);
        await device1.SyncAsync();

        var api = await server.SignedInClientAsync(Owner);
        var onServer = await api.GetFromJsonAsync<OutstandingReport>($"/api/companies/{id}/reports/outstanding?kind=Receivables&asAt={AsAt:yyyy-MM-dd}", SyncJson.Options);
        var on1 = await device1.RunAsync<BillWiseService, OutstandingReport>(b => b.OutstandingAsync(id, OutstandingKind.Receivables, AsAt));
        var on2 = await device2.RunAsync<BillWiseService, OutstandingReport>(b => b.OutstandingAsync(id, OutstandingKind.Receivables, AsAt));
        Assert.Empty(on1.Bills); // everything settled
        Assert.Equal(SyncJson.Serialize(on1), SyncJson.Serialize(onServer!));
        Assert.Equal(SyncJson.Serialize(on1), SyncJson.Serialize(on2));

        var serverPending = await api.GetFromJsonAsync<List<PendingBillDto>>($"/api/companies/{id}/ledgers/{s.Customer}/pending-bills", SyncJson.Options);
        Assert.Empty(serverPending!);
    }

    [Fact]
    public async Task Server_rejects_a_breakup_that_does_not_add_up()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var s = await CreateAsync(device);
        var receipt = await ReceiptAsync(device, s, 500m, new(2026, 6, 2), Agst("OPEN-7"));
        var stored = await device.QueryAsync(db => db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines).Include(v => v.BillAllocations).AsNoTracking().SingleAsync(v => v.Id == receipt.Id));
        await device.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == receipt.Id).ExecuteDeleteAsync());
        await device.SyncAsync();

        var snapshot = TallyNext.Application.Sync.SnapshotMapper.ToSnapshot(stored);
        var tampered = snapshot with { BillAllocations = snapshot.BillAllocations!.Select(a => a with { AmountMinor = a.AmountMinor - 1 }).ToList() };
        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var transport = device.Services.GetRequiredService<ISyncTransport>();
        var response = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 1, s.Books.CompanyId, SyncEntityTypes.Voucher, receipt.Id, SyncOperation.Upsert, 0, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(tampered)),
        ]), CancellationToken.None);

        Assert.Equal(SyncOutcome.Rejected, response.Results.Single().Outcome);
        Assert.Equal("bill.allocation_mismatch", response.Results.Single().ErrorCode);
    }

    [Fact]
    public async Task A_settlement_of_a_bill_the_server_never_received_is_accepted_and_shown_unmatched()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var s = await CreateAsync(device);
        var sale = await SaleAsync(device, s, 800m, new(2026, 6, 1));
        await ReceiptAsync(device, s, 300m, new(2026, 6, 5), Agst(sale.Number!));

        // The sale never reaches the server (e.g. its upload was discarded); the receipt still must not be lost.
        await device.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == sale.Id).ExecuteDeleteAsync());
        var report = await device.SyncAsync();
        Assert.Equal((0, 0), (report.Conflicts, report.Rejected));
        Assert.Equal(0, await device.QueryAsync(db => db.OutboxEntries.CountAsync(e => e.Status != OutboxStatus.Accepted)));

        var api = await server.SignedInClientAsync(Owner);
        var outstanding = await api.GetFromJsonAsync<OutstandingReport>($"/api/companies/{s.Books.CompanyId}/reports/outstanding?kind=Receivables&asAt={AsAt:yyyy-MM-dd}", SyncJson.Options);
        var unmatched = outstanding!.Bills.Single(b => b.BillName == sale.Number);
        Assert.Equal((-300m, -300m), (unmatched.Original, unmatched.Pending));
    }
}
