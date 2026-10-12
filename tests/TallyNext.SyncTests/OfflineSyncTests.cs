using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TallyNext.Contracts;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

/// <summary>End-to-end replication between real device databases and the real API (in memory).</summary>
public sealed class OfflineSyncTests
{
    private const string Owner = "owner@sync.test";

    [Fact]
    public async Task Voucher_saved_offline_uploads_on_reconnect_and_reaches_a_second_device()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device1 = await DeviceHost.StartAsync(server);
        await device1.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device1);

        // Network down: the voucher is saved locally, numbered in the device's own series, and queued.
        device1.Network.Mode = NetworkMode.Offline;
        var voucher = await books.PostPaymentAsync(device1, 1_500m);
        var series = await device1.SeriesCodeAsync();
        Assert.Equal($"{series}/1", voucher.Number);
        var offline = await Assert.ThrowsAsync<SyncTransportException>(() => device1.SyncAsync());
        Assert.Equal(SyncTransportFailure.Offline, offline.Failure);
        Assert.Equal(SyncState.Offline, device1.Status.Current.State);
        Assert.True(device1.Status.Current.PendingCount > 0);

        // Reconnect: everything uploads.
        device1.Network.Mode = NetworkMode.Online;
        var report = await device1.SyncAsync();
        Assert.True(report.Uploaded > 30, $"uploaded {report.Uploaded}");
        Assert.Equal(0, report.Conflicts);
        Assert.Equal(SyncState.UpToDate, device1.Status.Current.State);
        Assert.Equal(0, await device1.QueryAsync(db => db.OutboxEntries.CountAsync(e => e.Status != OutboxStatus.Accepted)));

        // Visible online through the API.
        var api = await server.SignedInClientAsync(Owner);
        var online = await api.GetFromJsonAsync<VoucherDto>($"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}", SyncJson.Options);
        Assert.Equal(voucher.Number, online!.Number);
        Assert.Equal(1_500m, online.Total);

        // A second device of the same user downloads the company, its masters and the voucher.
        await using var device2 = await DeviceHost.StartAsync(server);
        await device2.SignInAsync(Owner);
        var download = await device2.SyncAsync();
        Assert.True(download.Downloaded > 30);
        var books2 = await DeviceBooks.OpenAsync(device2, books.CompanyId);
        var copy = await device2.RunAsync<TallyNext.Application.Vouchers.VoucherService, VoucherDto>(s => s.GetAsync(books.CompanyId, voucher.Id));
        Assert.Equal(voucher.Number, copy.Number);
        Assert.Equal(voucher.Lines.Select(l => (l.Id, l.LedgerId, l.Amount)), copy.Lines.Select(l => (l.Id, l.LedgerId, l.Amount)));

        var tb1 = await books.TrialBalanceAsync(device1);
        var tb2 = await books2.TrialBalanceAsync(device2);
        Assert.Equal(tb1.TotalClosingDebit, tb2.TotalClosingDebit);
        Assert.Equal(tb1.Rows.Select(r => (r.Id, r.ClosingDebit, r.ClosingCredit)), tb2.Rows.Select(r => (r.Id, r.ClosingDebit, r.ClosingCredit)));

        // The second device's series differs, so both can number offline without collisions.
        Assert.NotEqual(series, await device2.SeriesCodeAsync());
        var fromDevice2 = await books2.PostPaymentAsync(device2, 10m);
        Assert.Equal($"{await device2.SeriesCodeAsync()}/1", fromDevice2.Number);
        await device2.SyncAsync();
        await device1.SyncAsync();
        var list = await device1.RunAsync<TallyNext.Application.Vouchers.VoucherService, PagedResult<VoucherListItem>>(s => s.ListAsync(books.CompanyId, new VoucherQuery()));
        Assert.Equal(2, list.TotalCount);
        Assert.Equal(2, list.Items.Select(v => v.Number).Distinct().Count());
    }

    [Fact]
    public async Task Lost_responses_and_retries_never_duplicate_postings()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        await device.SyncAsync();

        var voucher = await books.PostPaymentAsync(device, 777m);
        device.Network.Mode = NetworkMode.LoseResponses;
        await Assert.ThrowsAsync<SyncTransportException>(() => device.SyncAsync());
        var entry = await device.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == voucher.Id));
        Assert.Equal(OutboxStatus.Pending, entry.Status);
        Assert.Equal(1, entry.Attempts);

        device.Network.Mode = NetworkMode.Online;
        var report = await device.SyncAsync();
        Assert.Equal(1, report.Uploaded);
        entry = await device.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == voucher.Id));
        Assert.Equal(OutboxStatus.Accepted, entry.Status);
        Assert.True(entry.AcceptedVersion > 0);

        var api = await server.SignedInClientAsync(Owner);
        var vouchers = await api.GetFromJsonAsync<PagedResult<VoucherListItem>>($"/api/companies/{books.CompanyId}/vouchers", SyncJson.Options);
        Assert.Single(vouchers!.Items);
        var tb = await api.GetFromJsonAsync<TallyNext.Contracts.Reports.TrialBalanceReport>(
            $"/api/companies/{books.CompanyId}/reports/trial-balance?from=2026-04-01&to=2027-03-31", SyncJson.Options);
        Assert.Equal(777m, tb!.TotalPeriodDebit);
    }

    [Fact]
    public async Task Downloaded_changes_are_not_echoed_back_and_sync_is_quiescent()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        await books.PostPaymentAsync(device, 5m);

        await device.SyncAsync();
        var outboxAfterFirst = await device.QueryAsync(db => db.OutboxEntries.CountAsync());
        var second = await device.SyncAsync();
        var third = await device.SyncAsync();

        Assert.Equal(0, second.Uploaded);
        Assert.Equal(0, third.Uploaded);
        Assert.Equal(0, third.Downloaded);
        Assert.Equal(outboxAfterFirst, await device.QueryAsync(db => db.OutboxEntries.CountAsync()));
    }

    [Fact]
    public async Task Pending_work_survives_an_application_restart()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        var file = Path.Combine(Path.GetTempPath(), $"tallynext-restart-{Guid.NewGuid():N}.db");
        DeviceBooks books;
        Guid voucherId;
        var device = await DeviceHost.StartAsync(server, file);
        try
        {
            await device.SignInAsync(Owner);
            books = await DeviceBooks.CreateAsync(device);
            await device.SyncAsync();
            device.Network.Mode = NetworkMode.Offline;
            voucherId = (await books.PostPaymentAsync(device, 321m)).Id;
        }
        finally
        {
            await device.DisposeAsync();
        }

        // Restart while still offline: sign in offline, nothing is lost.
        var offlineNetwork = new NetworkSwitch { Mode = NetworkMode.Offline };
        var restarted = await DeviceHost.StartAsync(server, file, offlineNetwork);
        try
        {
            var signIn = await restarted.SignInAsync(Owner);
            Assert.Equal(TallyNext.Infrastructure.Local.Session.SignInMode.Offline, signIn.Mode);
            Assert.Equal(OutboxStatus.Pending, await restarted.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == voucherId).Select(e => e.Status).SingleAsync()));

            offlineNetwork.Mode = NetworkMode.Online;
            var report = await restarted.SyncAsync();
            Assert.Equal(1, report.Uploaded);
        }
        finally
        {
            await restarted.DisposeAsync();
            restarted.DeleteFiles();
        }

        var api = await server.SignedInClientAsync(Owner);
        var online = await api.GetFromJsonAsync<VoucherDto>($"/api/companies/{books.CompanyId}/vouchers/{voucherId}", SyncJson.Options);
        Assert.Equal(321m, online!.Total);
    }

    [Fact]
    public async Task Voucher_created_online_reaches_the_desktop()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device);
        await device.SyncAsync();

        var api = await server.SignedInClientAsync(Owner);
        var response = await api.PostAsJsonAsync($"/api/companies/{books.CompanyId}/vouchers", new VoucherInput
        {
            VoucherTypeId = books.Types[VoucherBaseType.Receipt],
            Date = new DateOnly(2026, 7, 1),
            Narration = "Created in the browser",
            Lines =
            [
                new VoucherLineInput { LedgerId = books.Cash, Side = DrCr.Debit, Amount = 250m },
                new VoucherLineInput { LedgerId = books.Customer, Side = DrCr.Credit, Amount = 250m },
            ],
        }, SyncJson.Options);
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<VoucherDto>(SyncJson.Options))!;
        Assert.Equal("1", created.Number); // server ("W") series

        var report = await device.SyncAsync();
        Assert.True(report.Downloaded >= 1);
        var local = await device.RunAsync<TallyNext.Application.Vouchers.VoucherService, VoucherDto>(s => s.GetAsync(books.CompanyId, created.Id));
        Assert.Equal("1", local.Number);
        Assert.Equal(250m, local.Total);
    }

    [Fact]
    public async Task Initial_download_of_a_larger_company_is_paged_and_complete()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device1 = await DeviceHost.StartAsync(server);
        await device1.SignInAsync(Owner);
        var books = await DeviceBooks.CreateAsync(device1);
        for (var i = 0; i < 260; i++)
        {
            await books.PostPaymentAsync(device1, 1m + i);
        }

        var upload = await device1.SyncAsync();
        Assert.True(upload.Uploaded > SyncProtocol.MaxPushBatchSize);

        await using var device2 = await DeviceHost.StartAsync(server);
        await device2.SignInAsync(Owner);
        var download = await device2.SyncAsync();
        Assert.True(download.Downloaded > SyncProtocol.DefaultPullBatchSize);
        Assert.Equal(260, await device2.QueryAsync(db => db.Vouchers.CountAsync()));
        var tb1 = await books.TrialBalanceAsync(device1);
        var tb2 = await books.TrialBalanceAsync(device2);
        Assert.Equal(tb1.TotalPeriodDebit, tb2.TotalPeriodDebit);
        Assert.Equal(Enumerable.Range(0, 260).Sum(i => 1m + i), tb2.TotalPeriodDebit);
    }
}
