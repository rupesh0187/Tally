using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Application.Accounts;
using TallyNext.Application.Companies;
using TallyNext.Application.Inventory;
using TallyNext.Application.Reports;
using TallyNext.Application.Tax;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

/// <summary>Inventory masters and GST invoices created offline replicate exactly and are re-validated by the server.</summary>
public sealed class InventorySyncTests
{
    private const string Owner = "owner@inventory-sync.test";
    private static readonly DateOnly FyStart = DeviceGstBooks.FyStart;
    private static readonly DateOnly FyEnd = DeviceGstBooks.FyEnd;

    [Fact]
    public async Task Inventory_and_gst_invoices_made_offline_replicate_to_the_server_and_other_devices()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device1 = await DeviceHost.StartAsync(server);
        await device1.SignInAsync(Owner);

        device1.Network.Mode = NetworkMode.Offline;
        var setup = await DeviceGstBooks.CreateAsync(device1);
        var id = setup.Books.CompanyId;
        var local = await device1.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(id, setup.Sale(setup.LocalCustomer, 2, 300m, new(2026, 6, 1))));
        var interState = await device1.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(id, setup.Sale(setup.OutOfStateCustomer, 1, 500m, new(2026, 6, 2))));
        Assert.Equal(SupplyNature.IntraState, local.Invoice!.SupplyNature);
        Assert.Equal(SupplyNature.InterState, interState.Invoice!.SupplyNature);

        device1.Network.Mode = NetworkMode.Online;
        var report = await device1.SyncAsync();
        Assert.Equal(0, report.Conflicts);
        var notAccepted = await device1.QueryAsync(db => db.OutboxEntries.Where(e => e.Status != OutboxStatus.Accepted)
            .Select(e => e.EntityType + ":" + e.Status + ":" + e.LastErrorCode + ":" + e.LastError).ToListAsync());
        Assert.True(notAccepted.Count == 0, string.Join(Environment.NewLine, notAccepted.Take(5)));

        // The server holds the invoices with their lines and computes the same reports.
        var api = await server.SignedInClientAsync(Owner);
        var online = await api.GetFromJsonAsync<VoucherDto>($"/api/companies/{id}/vouchers/{local.Id}", SyncJson.Options);
        Assert.Equal(708m, online!.Total);
        Assert.Equal((54m, 54m), (online.Invoice!.Cgst, online.Invoice.StateTax));
        Assert.Equal(local.Invoice.Lines.Select(l => (l.Id, l.Quantity, l.Taxable)), online.Invoice.Lines.Select(l => (l.Id, l.Quantity, l.Taxable)));

        var query = $"from={FyStart:yyyy-MM-dd}&to={FyEnd:yyyy-MM-dd}";
        var serverStock = await api.GetFromJsonAsync<StockSummaryReport>($"/api/companies/{id}/reports/stock-summary?{query}", SyncJson.Options);
        var serverGst = await api.GetFromJsonAsync<GstSummaryReport>($"/api/companies/{id}/reports/gst-summary?{query}", SyncJson.Options);
        var deviceStock = await device1.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(id, FyStart, FyEnd));
        var deviceGst = await device1.RunAsync<ReportService, GstSummaryReport>(s => s.GstSummaryAsync(id, FyStart, FyEnd));
        Assert.Equal(2m, Assert.Single(deviceStock.Rows).ClosingQuantity);
        Assert.Equal(400m, deviceStock.TotalClosingValue);
        Assert.Equal(deviceStock.TotalClosingValue, serverStock!.TotalClosingValue);
        Assert.Equal(deviceStock.Rows.Select(r => (r.StockItemId, r.ClosingQuantity, r.OutwardCost)), serverStock.Rows.Select(r => (r.StockItemId, r.ClosingQuantity, r.OutwardCost)));
        Assert.Equal((198m, 0m), (deviceGst.OutputTax, deviceGst.InputTax));
        Assert.Equal(deviceGst.Outward, serverGst!.Outward);

        // A second device downloads masters and invoices and reports the same figures.
        await using var device2 = await DeviceHost.StartAsync(server);
        await device2.SignInAsync(Owner);
        await device2.SyncAsync();
        var copy = await device2.RunAsync<VoucherService, VoucherDto>(s => s.GetAsync(id, interState.Id));
        Assert.Equal(interState.Invoice.Igst, copy.Invoice!.Igst);
        Assert.Equal("Sync Widget", copy.Invoice.Lines.Single().ItemName);
        var pl1 = await device1.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(id, FyStart, FyEnd));
        var pl2 = await device2.RunAsync<ReportService, ProfitAndLossReport>(s => s.ProfitAndLossAsync(id, FyStart, FyEnd));
        Assert.Equal(500m, pl1.GrossProfit); // sales 1,100 - cost 600 (3 @ 200)
        Assert.Equal((pl1.GrossProfit, pl1.ClosingStock), (pl2.GrossProfit, pl2.ClosingStock));

        // Device 2 sells the remaining stock offline while device 1 does the same: both are accepted, stock goes
        // negative (shown as such) rather than one sale being lost.
        device1.Network.Mode = NetworkMode.Offline;
        device2.Network.Mode = NetworkMode.Offline;
        var books2 = setup with { Books = await DeviceBooks.OpenAsync(device2, id) };
        await device1.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(id, setup.Sale(setup.LocalCustomer, 2, 300m, new(2026, 7, 1))));
        await device2.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(id, books2.Sale(setup.LocalCustomer, 2, 300m, new(2026, 7, 1))));
        device1.Network.Mode = NetworkMode.Online;
        device2.Network.Mode = NetworkMode.Online;
        Assert.Equal(0, (await device1.SyncAsync()).Conflicts);
        Assert.Equal(0, (await device2.SyncAsync()).Conflicts);
        await device1.SyncAsync();

        var after1 = Assert.Single((await device1.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(id, FyStart, FyEnd))).Rows);
        var after2 = Assert.Single((await device2.RunAsync<ReportService, StockSummaryReport>(s => s.StockSummaryAsync(id, FyStart, FyEnd))).Rows);
        Assert.Equal(-2m, after1.ClosingQuantity);
        Assert.True(after1.WentNegative);
        Assert.Equal((after1.ClosingQuantity, after1.ClosingValue), (after2.ClosingQuantity, after2.ClosingValue));
    }

    [Fact]
    public async Task Server_rejects_an_invoice_whose_tax_was_altered_consistently_on_the_device()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var setup = await DeviceGstBooks.CreateAsync(device);
        var id = setup.Books.CompanyId;
        var invoice = await device.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(id, setup.Sale(setup.LocalCustomer, 1, 1_000m, new(2026, 6, 1))));

        // Upload everything except the invoice, then send a balanced but under-taxed version of it: CGST and SGST
        // each lowered by 10.00 on the item line, the tax lines and the party total.
        var stored = await device.QueryAsync(db => db.Vouchers.Include(v => v.Lines).Include(v => v.ItemLines).AsNoTracking().SingleAsync(v => v.Id == invoice.Id));
        await device.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == invoice.Id).ExecuteDeleteAsync());
        await device.SyncAsync();

        var ledgers = await device.QueryAsync(db => db.Ledgers.AsNoTracking().Where(l => l.CompanyId == id).ToDictionaryAsync(l => l.Id, l => l.GstTaxType));
        var snapshot = TallyNext.Application.Sync.SnapshotMapper.ToSnapshot(stored);
        var tampered = snapshot with
        {
            TotalMinor = snapshot.TotalMinor - 2_000,
            ItemLines = snapshot.ItemLines.Select(l => l with { CgstMinor = l.CgstMinor - 1_000, StateTaxMinor = l.StateTaxMinor - 1_000 }).ToList(),
            Lines = snapshot.Lines.Select(l =>
                l.LedgerId == setup.LocalCustomer ? l with { AmountMinor = l.AmountMinor - 2_000 }
                : ledgers[l.LedgerId] is GstTaxType.Cgst or GstTaxType.Sgst ? l with { AmountMinor = l.AmountMinor - 1_000 }
                : l).ToList(),
        };
        Assert.Equal(
            tampered.Lines.Where(l => l.Side == DrCr.Debit).Sum(l => l.AmountMinor),
            tampered.Lines.Where(l => l.Side == DrCr.Credit).Sum(l => l.AmountMinor));

        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var transport = device.Services.GetRequiredService<ISyncTransport>();
        var response = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 1, id, SyncEntityTypes.Voucher, invoice.Id, SyncOperation.Upsert, 0, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(tampered)),
        ]), CancellationToken.None);

        var result = response.Results.Single();
        Assert.Equal(SyncOutcome.Rejected, result.Outcome);
        Assert.Equal("invoice.tax_mismatch", result.ErrorCode);

        // The genuine version is still accepted.
        var genuine = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 2, id, SyncEntityTypes.Voucher, invoice.Id, SyncOperation.Upsert, 0, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(snapshot)),
        ]), CancellationToken.None);
        Assert.Equal(SyncOutcome.Accepted, genuine.Results.Single().Outcome);
    }

    [Fact]
    public async Task Server_rejects_stock_items_with_invalid_gst_rates()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Owner);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(Owner);
        var setup = await DeviceGstBooks.CreateAsync(device);
        await device.SyncAsync();

        var stored = await device.QueryAsync(db => db.StockItems.AsNoTracking().SingleAsync(i => i.Id == setup.Widget));
        var snapshot = TallyNext.Application.Sync.SnapshotMapper.ToSnapshot(stored);
        var tampered = snapshot with { GstRates = [new GstRateEntry(FyStart, Taxability.Exempt, 18_000, 0, null)] };
        var deviceId = await device.QueryAsync(db => db.DeviceRegistrations.Select(r => r.DeviceId).SingleAsync());
        var transport = device.Services.GetRequiredService<ISyncTransport>();
        var response = await transport.PushAsync(new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion,
        [
            new SyncChangeUpload(Guid.CreateVersion7(), 1, setup.Books.CompanyId, SyncEntityTypes.StockItem, stored.Id, SyncOperation.Upsert, stored.Version, SyncProtocol.SchemaVersion, DateTime.UtcNow, SyncJson.ToElement(tampered)),
        ]), CancellationToken.None);

        Assert.Equal(SyncOutcome.Rejected, response.Results.Single().Outcome);
        Assert.Equal("gst.rate_for_untaxed", response.Results.Single().ErrorCode);
    }
}
