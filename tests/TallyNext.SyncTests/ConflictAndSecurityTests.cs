using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Accounts;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

public sealed class ConflictAndSecurityTests
{
    private const string Owner = "owner@conflict.test";

    private static async Task<(ApiServer Server, DeviceHost D1, DeviceHost D2, DeviceBooks Books)> TwoSyncedDevicesAsync()
    {
        var server = new ApiServer();
        await server.RegisterAsync(Owner);
        var d1 = await DeviceHost.StartAsync(server);
        await d1.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(d1);
        await d1.SyncAsync();
        var d2 = await DeviceHost.StartAsync(server);
        await d2.SignInAsync(Owner);
        await d2.SyncAsync();
        return (server, d1, d2, books);
    }

    private static Task<LedgerDto> RenameAsync(DeviceHost device, Guid companyId, Guid ledgerId, string name) =>
        device.RunAsync<ChartOfAccountsService, LedgerDto>(async s =>
        {
            var ledger = await s.GetLedgerAsync(companyId, ledgerId);
            return await s.UpdateLedgerAsync(companyId, ledgerId, new LedgerInput { Name = name, GroupId = ledger.GroupId, OpeningBalance = ledger.OpeningBalance, OpeningSide = ledger.OpeningSide });
        });

    [Fact]
    public async Task Concurrent_edits_produce_a_conflict_that_must_be_resolved_explicitly()
    {
        var (server, d1, d2, books) = await TwoSyncedDevicesAsync();
        await using var _s = server;
        await using var _1 = d1;
        await using var _2 = d2;

        await RenameAsync(d1, books.CompanyId, books.Customer, "Customer (renamed on device 1)");
        await d1.SyncAsync();
        d2.Network.Mode = NetworkMode.Offline;
        await RenameAsync(d2, books.CompanyId, books.Customer, "Customer (renamed on device 2)");
        d2.Network.Mode = NetworkMode.Online;

        var report = await d2.SyncAsync();
        Assert.Equal(1, report.Conflicts);
        Assert.Equal(SyncState.NeedsAttention, d2.Status.Current.State);
        var conflict = (await d2.RunAsync<ConflictResolver, IReadOnlyList<LocalConflictDto>>(r => r.ListAsync(books.CompanyId))).Single();
        Assert.Equal("Conflict", conflict.Kind);
        Assert.Contains("device 1", conflict.ServerPayloadJson, StringComparison.Ordinal);
        Assert.Contains("device 2", conflict.LocalPayloadJson, StringComparison.Ordinal);

        // The server kept device 1's value and recorded the conflict for review.
        var api = await server.SignedInClientAsync(Owner);
        var serverLedger = await api.GetFromJsonAsync<LedgerDto>($"/api/companies/{books.CompanyId}/ledgers/{books.Customer}", SyncJson.Options);
        Assert.Equal("Customer (renamed on device 1)", serverLedger!.Name);
        var serverConflicts = await api.GetFromJsonAsync<List<SyncConflictDto>>($"/api/companies/{books.CompanyId}/sync/conflicts", SyncJson.Options);
        Assert.Single(serverConflicts!, c => c.Status == SyncConflictStatus.Open);

        // Device 2's user explicitly keeps their version; it is applied on top of the server's.
        await d2.RunAsync<ConflictResolver>(r => r.KeepLocalVersionAsync(books.CompanyId, conflict.Id));
        report = await d2.SyncAsync();
        Assert.Equal(1, report.Uploaded);
        serverLedger = await api.GetFromJsonAsync<LedgerDto>($"/api/companies/{books.CompanyId}/ledgers/{books.Customer}", SyncJson.Options);
        Assert.Equal("Customer (renamed on device 2)", serverLedger!.Name);
        serverConflicts = await api.GetFromJsonAsync<List<SyncConflictDto>>($"/api/companies/{books.CompanyId}/sync/conflicts", SyncJson.Options);
        Assert.Equal(SyncConflictStatus.ResolvedKeepClient, serverConflicts!.Single().Status);

        await d1.SyncAsync();
        var onDevice1 = await d1.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.GetLedgerAsync(books.CompanyId, books.Customer));
        Assert.Equal("Customer (renamed on device 2)", onDevice1.Name);
    }

    [Fact]
    public async Task Choosing_the_server_version_replaces_the_local_edit_and_keeps_it_for_review()
    {
        var (server, d1, d2, books) = await TwoSyncedDevicesAsync();
        await using var _s = server;
        await using var _1 = d1;
        await using var _2 = d2;

        await RenameAsync(d1, books.CompanyId, books.Rent, "Rent (server wins)");
        await d1.SyncAsync();
        await RenameAsync(d2, books.CompanyId, books.Rent, "Rent (local loses)");
        await d2.SyncAsync();

        var conflict = (await d2.RunAsync<ConflictResolver, IReadOnlyList<LocalConflictDto>>(r => r.ListAsync(books.CompanyId))).Single();
        await d2.RunAsync<ConflictResolver>(r => r.KeepServerVersionAsync(books.CompanyId, conflict.Id));

        var local = await d2.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.GetLedgerAsync(books.CompanyId, books.Rent));
        Assert.Equal("Rent (server wins)", local.Name);
        var discarded = await d2.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == books.Rent && e.Status == OutboxStatus.Discarded));
        Assert.Contains("Rent (local loses)", discarded.PayloadJson, StringComparison.Ordinal);
        var report = await d2.SyncAsync();
        Assert.Equal(0, report.Uploaded);
        Assert.Equal(SyncState.UpToDate, d2.Status.Current.State);
    }

    [Fact]
    public async Task A_voucher_reversed_on_two_offline_devices_is_reversed_only_once()
    {
        var (server, d1, d2, books) = await TwoSyncedDevicesAsync();
        await using var _s = server;
        await using var _1 = d1;
        await using var _2 = d2;

        var original = await books.PostPaymentAsync(d1, 400m);
        await d1.SyncAsync();
        await d2.SyncAsync();

        d1.Network.Mode = NetworkMode.Offline;
        d2.Network.Mode = NetworkMode.Offline;
        var request = new ReverseVoucherRequest { Date = new DateOnly(2026, 6, 2) };
        await d1.RunAsync<VoucherService, VoucherDto>(s => s.ReverseAsync(books.CompanyId, original.Id, request));
        var reversal2 = await d2.RunAsync<VoucherService, VoucherDto>(s => s.ReverseAsync(books.CompanyId, original.Id, request));
        d1.Network.Mode = NetworkMode.Online;
        d2.Network.Mode = NetworkMode.Online;

        Assert.Equal(1, (await d1.SyncAsync()).Uploaded);
        var report = await d2.SyncAsync();
        Assert.Equal(0, report.Uploaded);
        var conflict = (await d2.RunAsync<ConflictResolver, IReadOnlyList<LocalConflictDto>>(r => r.ListAsync(books.CompanyId))).Single();
        Assert.Equal("voucher.already_reversed", conflict.ErrorCode);

        // Until resolved, device 2 still shows its own reversal (never silently dropped).
        Assert.Equal(VoucherStatus.Posted, (await d2.RunAsync<VoucherService, VoucherDto>(s => s.GetAsync(books.CompanyId, reversal2.Id))).Status);

        await d2.RunAsync<ConflictResolver>(r => r.KeepServerVersionAsync(books.CompanyId, conflict.Id));
        await d2.SyncAsync();
        var tb1 = await books.TrialBalanceAsync(d1);
        var tb2 = await books.TrialBalanceAsync(d2);
        Assert.Equal(tb1.Rows.Select(r => (r.Id, r.ClosingDebit, r.ClosingCredit)), tb2.Rows.Select(r => (r.Id, r.ClosingDebit, r.ClosingCredit)));
        var rent = tb2.Rows.Single(r => r.Id == books.Rent);
        Assert.Equal(0m, rent.ClosingDebit);
    }

    [Fact]
    public async Task A_record_deleted_on_the_server_cannot_be_resurrected_by_a_stale_device()
    {
        var (server, d1, d2, books) = await TwoSyncedDevicesAsync();
        await using var _s = server;
        await using var _1 = d1;
        await using var _2 = d2;

        var spare = await d1.RunAsync<ChartOfAccountsService, LedgerDto>(async s =>
        {
            var groups = await s.ListGroupsAsync(books.CompanyId);
            return await s.CreateLedgerAsync(books.CompanyId, new LedgerInput { Name = "Spare Ledger", GroupId = groups.Single(g => g.Name == "Indirect Expenses").Id });
        });
        await d1.SyncAsync();
        await d2.SyncAsync();

        await d1.RunAsync<ChartOfAccountsService>(s => s.DeleteLedgerAsync(books.CompanyId, spare.Id));
        await d1.SyncAsync();
        await RenameAsync(d2, books.CompanyId, spare.Id, "Spare Ledger (edited while stale)");
        var report = await d2.SyncAsync();
        Assert.Equal(1, report.Conflicts);
        var conflict = (await d2.RunAsync<ConflictResolver, IReadOnlyList<LocalConflictDto>>(r => r.ListAsync(books.CompanyId))).Single();
        Assert.Equal("sync.row_deleted", conflict.ErrorCode);

        var api = await server.SignedInClientAsync(Owner);
        var response = await api.GetAsync($"/api/companies/{books.CompanyId}/ledgers/{spare.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await d2.RunAsync<ConflictResolver>(r => r.KeepServerVersionAsync(books.CompanyId, conflict.Id));
        Assert.True(await d2.QueryAsync(db => db.Ledgers.Where(l => l.Id == spare.Id).Select(l => l.IsDeleted).SingleAsync()));
    }

    [Fact]
    public async Task A_posted_voucher_cannot_be_overwritten_through_sync()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        var voucher = await books.PostPaymentAsync(device, 100m);
        await device.SyncAsync();

        // Craft an upload that rewrites the posted voucher's amounts (a tampered or buggy client).
        var stored = await device.QueryAsync(db => db.Vouchers.Include(v => v.Lines).AsNoTracking().SingleAsync(v => v.Id == voucher.Id));
        var snapshot = TallyNext.Application.Sync.SnapshotMapper.ToSnapshot(stored);
        var tampered = snapshot with
        {
            TotalMinor = 1,
            Lines = snapshot.Lines.Select(l => l with { AmountMinor = 1 }).ToList(),
        };
        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var transport = device.Services.GetRequiredService<ISyncTransport>();
        var response = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 1, books.CompanyId, SyncEntityTypes.Voucher, voucher.Id, SyncOperation.Upsert,
                stored.Version, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(tampered)),
        ]), CancellationToken.None);

        var result = response.Results.Single();
        Assert.Equal(SyncOutcome.Rejected, result.Outcome);
        Assert.Equal("voucher.posted_immutable", result.ErrorCode);

        var api = await server.SignedInClientAsync(Owner);
        var online = await api.GetFromJsonAsync<VoucherDto>($"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}", SyncJson.Options);
        Assert.Equal(100m, online!.Total);
    }

    [Fact]
    public async Task Unbalanced_or_foreign_series_vouchers_from_a_device_are_rejected_by_the_server()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        var voucher = await books.PostPaymentAsync(device, 50m);
        var stored = await device.QueryAsync(db => db.Vouchers.Include(v => v.Lines).AsNoTracking().SingleAsync(v => v.Id == voucher.Id));
        await device.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == voucher.Id).ExecuteDeleteAsync());
        await device.SyncAsync();

        var snapshot = TallyNext.Application.Sync.SnapshotMapper.ToSnapshot(stored);
        var unbalanced = snapshot with { Lines = [snapshot.Lines[0], snapshot.Lines[1] with { AmountMinor = 4_999 }] };
        var foreignSeries = snapshot with { Id = Guid.CreateVersion7(), SeriesCode = "W", Number = "99", SequenceNumber = 99, Lines = snapshot.Lines.Select(l => l with { Id = Guid.CreateVersion7() }).ToList() };
        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var transport = device.Services.GetRequiredService<ISyncTransport>();
        var response = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 1, books.CompanyId, SyncEntityTypes.Voucher, unbalanced.Id, SyncOperation.Upsert, 0, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(unbalanced)),
            new SyncChangeUpload(Guid.CreateVersion7(), 2, books.CompanyId, SyncEntityTypes.Voucher, foreignSeries.Id, SyncOperation.Upsert, 0, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(foreignSeries)),
        ]), CancellationToken.None);

        Assert.Equal(["voucher.unbalanced", "voucher.series_not_owned"], response.Results.Select(r => r.ErrorCode));
        Assert.All(response.Results, r => Assert.Equal(SyncOutcome.Rejected, r.Outcome));
    }

    [Fact]
    public async Task Revoked_member_cannot_upload_and_loses_access_but_keeps_local_data()
    {
        const string member = "member@conflict.test";
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await server.RegisterAsync(member);
        await using var ownerDevice = await DeviceHost.StartAsync(server);
        await ownerDevice.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(ownerDevice);
        await ownerDevice.SyncAsync();

        var ownerApi = await server.SignedInClientAsync(Owner);
        var added = await ownerApi.PostAsJsonAsync($"/api/companies/{books.CompanyId}/members", new AddMemberRequest { Email = member, Role = CompanyRole.Accountant }, SyncJson.Options);
        added.EnsureSuccessStatusCode();
        var membership = (await added.Content.ReadFromJsonAsync<MemberDto>(SyncJson.Options))!;

        await using var memberDevice = await DeviceHost.StartAsync(server);
        await memberDevice.SignInAsync(member);
        await memberDevice.SyncAsync();
        var memberBooks = await DeviceBooks.OpenAsync(memberDevice, books.CompanyId);

        // Revoked while the member's device has unsent work.
        memberDevice.Network.Mode = NetworkMode.Offline;
        var pending = await memberBooks.PostPaymentAsync(memberDevice, 60m);
        (await ownerApi.DeleteAsync($"/api/companies/{books.CompanyId}/members/{membership.MembershipId}")).EnsureSuccessStatusCode();
        memberDevice.Network.Mode = NetworkMode.Online;

        var report = await memberDevice.SyncAsync();
        Assert.Equal(0, report.Uploaded);
        Assert.Contains(books.CompanyId, report.RevokedCompanies);
        var entry = await memberDevice.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == pending.Id));
        Assert.Equal(OutboxStatus.Forbidden, entry.Status);

        // The server never received the voucher; the device keeps it for review but the company is now inaccessible.
        var check = await ownerApi.GetAsync($"/api/companies/{books.CompanyId}/vouchers/{pending.Id}");
        Assert.Equal(HttpStatusCode.NotFound, check.StatusCode);
        await Assert.ThrowsAsync<TallyNext.Application.Common.NotFoundException>(() =>
            memberDevice.RunAsync<VoucherService, VoucherDto>(s => s.GetAsync(books.CompanyId, pending.Id)));
        Assert.True(await memberDevice.QueryAsync(db => db.Vouchers.AnyAsync(v => v.Id == pending.Id)));
    }

    [Fact]
    public async Task Reusing_a_change_id_with_different_content_is_rejected()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        await device.SyncAsync();

        var entry = await device.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityType == SyncEntityTypes.Ledger && e.PayloadJson.Contains("Sync Bank A/c")));
        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var payload = JsonDocument.Parse(entry.PayloadJson.Replace("Sync Bank A/c", "Something else", StringComparison.Ordinal)).RootElement.Clone();
        var response = await device.Services.GetRequiredService<ISyncTransport>().PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(entry.ChangeId, entry.LocalSequence, entry.CompanyId, entry.EntityType, entry.EntityId, entry.Operation, entry.BaseVersion, entry.SchemaVersion, entry.ClientModifiedAtUtc, payload),
        ]), CancellationToken.None);
        Assert.Equal("sync.change_id_reused", response.Results.Single().ErrorCode);
    }
}
