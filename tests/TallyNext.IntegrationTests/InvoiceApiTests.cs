using System.Net;
using System.Net.Http.Json;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;
using TallyNext.SyncTests.Support;

namespace TallyNext.IntegrationTests;

/// <summary>GST and inventory through the REST API: masters, invoice preview and creation, reports.</summary>
public sealed class InvoiceApiTests
{
    private const string User = "owner@invoices.test";

    [Fact]
    public async Task Invoice_endpoints_compute_gst_and_stock_and_retries_are_idempotent()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        var client = await server.SignInAsync(User);
        var books = await ApiClient.CreateBooksAsync(client); // Delhi (07)
        var company = $"/api/companies/{books.CompanyId}";

        // GST registration needs the GSTIN in the profile first.
        var profile = await (await client.GetAsync(company)).ReadAsync<CompanyDto>();
        var first14 = "07AAACA1234B1Z";
        var update = new UpdateCompanyRequest { Name = profile.Name, StateCode = "07", Gstin = first14 + Gstin.ComputeCheckCharacter(first14), ExpectedVersion = profile.Version };
        (await client.PutAsJsonAsync(company, update, SyncJson.Options)).EnsureSuccessStatusCode();
        var registered = await (await client.PutAsJsonAsync($"{company}/gst/registration", new SetGstRegistrationRequest(), SyncJson.Options)).ReadAsync<CompanyDto>();
        Assert.Equal(GstRegistrationType.Regular, registered.GstRegistration);

        var groups = await (await client.GetAsync($"{company}/groups")).ReadAsync<List<AccountGroupDto>>();
        async Task<Guid> Ledger(LedgerInput input) => (await (await client.PostJsonAsync($"{company}/ledgers", input)).ReadAsync<LedgerDto>()).Id;
        var sales = await Ledger(new LedgerInput { Name = "API Sales", GroupId = groups.Single(g => g.Name == "Sales Accounts").Id });
        var customer = await Ledger(new LedgerInput { Name = "Noida Buyer", GroupId = groups.Single(g => g.Name == "Sundry Debtors").Id, GstRegistrationType = GstRegistrationType.Consumer, StateCode = "09" });

        var units = await (await client.GetAsync($"{company}/inventory/units")).ReadAsync<List<UnitDto>>();
        var godowns = await (await client.GetAsync($"{company}/inventory/godowns")).ReadAsync<List<GodownDto>>();
        var item = await (await client.PostJsonAsync($"{company}/inventory/items", new StockItemInput
        {
            Name = "API Lamp",
            UnitId = units.Single(u => u.Symbol == "Nos").Id,
            GstRates = [new GstRateInput { EffectiveFrom = new DateOnly(2026, 4, 1), IgstPercent = 12m, HsnSac = "9405" }],
            OpeningQuantity = 20,
            OpeningValue = 2_000m,
            OpeningGodownId = godowns.Single(g => g.IsSystem).Id,
        })).ReadAsync<StockItemDto>();

        var types = await (await client.GetAsync($"{company}/voucher-types")).ReadAsync<List<VoucherTypeDto>>();
        var request = new InvoiceRequest
        {
            VoucherTypeId = types.Single(t => t.BaseType == VoucherBaseType.Sales && t.IsSystem).Id,
            Date = new DateOnly(2026, 5, 6),
            PartyLedgerId = customer,
            Lines = [new InvoiceRequestLine { StockItemId = item.Id, Quantity = 3, Rate = 333.33m, LedgerId = sales }],
        };

        var preview = await (await client.PostJsonAsync($"{company}/invoices/preview", request)).ReadAsync<InvoicePreview>();
        Assert.Equal(SupplyNature.InterState, preview.SupplyNature); // Delhi -> Uttar Pradesh
        Assert.Equal((999.99m, 120m, 1_120m), (preview.Taxable, preview.Igst, preview.GrandTotal)); // 119.9988 -> 120.00; 1,119.99 -> 1,120

        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{company}/invoices") { Content = JsonContent.Create(request, options: SyncJson.Options) };
            message.Headers.Add("Idempotency-Key", "invoice-retry-7");
            var response = await client.SendAsync(message);
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, response.StatusCode.ToString());
            var voucher = await response.ReadAsync<VoucherDto>();
            Assert.Equal(preview.GrandTotal, voucher.Invoice!.GrandTotal);
            ids.Add(voucher.Id);
        }

        Assert.Single(ids.Distinct());

        var range = "from=2026-04-01&to=2027-03-31";
        var stock = await (await client.GetAsync($"{company}/reports/stock-summary?{range}")).ReadAsync<StockSummaryReport>();
        Assert.Equal((17m, 1_700m), (stock.Rows.Single().ClosingQuantity, stock.Rows.Single().ClosingValue));
        var gst = await (await client.GetAsync($"{company}/reports/gst-summary?{range}")).ReadAsync<GstSummaryReport>();
        Assert.Equal(120m, gst.OutputTax);

        // A line without a quantity is a validation problem, not a server error.
        request.Lines[0].Quantity = 0;
        request.IdempotencyKey = null;
        var invalid = await client.PostJsonAsync($"{company}/invoices", request);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_create_inventory_masters()
    {
        const string viewer = "viewer@invoices.test";
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        await server.RegisterAsync(viewer);
        var owner = await server.SignInAsync(User);
        var books = await ApiClient.CreateBooksAsync(owner);
        (await owner.PostJsonAsync($"/api/companies/{books.CompanyId}/members", new AddMemberRequest { Email = viewer, Role = CompanyRole.Viewer })).EnsureSuccessStatusCode();

        var client = await server.SignInAsync(viewer);
        var units = await (await client.GetAsync($"/api/companies/{books.CompanyId}/inventory/units")).ReadAsync<List<UnitDto>>();
        Assert.NotEmpty(units);
        var denied = await client.PostJsonAsync($"/api/companies/{books.CompanyId}/inventory/units", new UnitInput { Symbol = "Box", Name = "Boxes" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
