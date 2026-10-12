using TallyNext.Application.Accounts;
using TallyNext.Application.Companies;
using TallyNext.Application.Inventory;
using TallyNext.Application.Tax;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Inventory;
using TallyNext.Domain.Vouchers;

namespace TallyNext.SyncTests.Support;

/// <summary>
/// A GST-registered synthetic company in Karnataka created on a device: an 18% item (opening 5 @ 200), parties in
/// and outside the State, sales and purchase ledgers. GSTINs are synthetic (valid format and checksum only).
/// </summary>
public sealed record DeviceGstBooks(DeviceBooks Books, Guid Sales, Guid Purchases, Guid LocalCustomer, Guid OutOfStateCustomer, Guid Supplier, Guid Widget)
{
    public static readonly DateOnly FyStart = new(2026, 4, 1);
    public static readonly DateOnly FyEnd = new(2027, 3, 31);

    public Guid CompanyId => Books.CompanyId;

    public static string SyntheticGstin(string stateCode, string pan)
    {
        var first14 = stateCode + pan + "1Z";
        return first14 + Gstin.ComputeCheckCharacter(first14);
    }

    public static async Task<DeviceGstBooks> CreateAsync(DeviceHost device, ValuationMethod valuation = ValuationMethod.WeightedAverage)
    {
        var books = await DeviceBooks.CreateAsync(device);
        var id = books.CompanyId;
        var company = await device.RunAsync<CompanyService, CompanyDto>(s => s.GetAsync(id));
        await device.RunAsync<CompanyService, CompanyDto>(s => s.UpdateAsync(id, new UpdateCompanyRequest
        {
            Name = company.Name,
            StateCode = "29",
            Gstin = SyntheticGstin("29", "AAACS1234F"),
            ExpectedVersion = company.Version,
        }));
        await device.RunAsync<GstService, CompanyDto>(s => s.SetRegistrationAsync(id, new SetGstRegistrationRequest()));

        var groups = await device.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(id));
        Guid Group(string n) => groups.Single(g => g.Name == n).Id;
        async Task<Guid> Ledger(LedgerInput input) => (await device.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(id, input))).Id;
        var sales = await Ledger(new LedgerInput { Name = "Sync Sales", GroupId = Group("Sales Accounts") });
        var purchases = await Ledger(new LedgerInput { Name = "Sync Purchases", GroupId = Group("Purchase Accounts") });
        var local = await Ledger(new LedgerInput
        {
            Name = "Bengaluru Retailer",
            GroupId = Group("Sundry Debtors"),
            GstRegistrationType = GstRegistrationType.Regular,
            StateCode = "29",
            Gstin = SyntheticGstin("29", "AAACL5678K"),
        });
        var outOfState = await Ledger(new LedgerInput { Name = "Pune Consumer", GroupId = Group("Sundry Debtors"), GstRegistrationType = GstRegistrationType.Consumer, StateCode = "27" });
        var supplier = await Ledger(new LedgerInput
        {
            Name = "Mysuru Supplier",
            GroupId = Group("Sundry Creditors"),
            GstRegistrationType = GstRegistrationType.Regular,
            StateCode = "29",
            Gstin = SyntheticGstin("29", "AABCT9012M"),
        });

        var units = await device.RunAsync<InventoryService, IReadOnlyList<UnitDto>>(s => s.ListUnitsAsync(id));
        var godowns = await device.RunAsync<InventoryService, IReadOnlyList<GodownDto>>(s => s.ListGodownsAsync(id));
        var widget = await device.RunAsync<InventoryService, StockItemDto>(s => s.SaveItemAsync(id, null, new StockItemInput
        {
            Name = "Sync Widget",
            UnitId = units.Single(u => u.Symbol == "Nos").Id,
            ValuationMethod = valuation,
            GstRates = [new GstRateInput { EffectiveFrom = FyStart, IgstPercent = 18m, HsnSac = "8471" }],
            OpeningQuantity = 5,
            OpeningValue = 1_000m,
            OpeningGodownId = godowns.Single(g => g.IsSystem).Id,
        }));
        return new DeviceGstBooks(books, sales, purchases, local, outOfState, supplier, widget.Id);
    }

    public InvoiceRequest Invoice(VoucherBaseType type, Guid party, DateOnly date, params (Guid Item, decimal Quantity, decimal Rate)[] lines) => new()
    {
        VoucherTypeId = Books.Types[type],
        Date = date,
        PartyLedgerId = party,
        RoundOff = false,
        Lines = lines.Select(l => new InvoiceRequestLine
        {
            StockItemId = l.Item,
            Quantity = l.Quantity,
            Rate = l.Rate,
            LedgerId = type is VoucherBaseType.Purchase or VoucherBaseType.DebitNote ? Purchases : Sales,
        }).ToList(),
    };

    public InvoiceRequest Sale(Guid party, decimal quantity, decimal rate, DateOnly date) =>
        Invoice(VoucherBaseType.Sales, party, date, (Widget, quantity, rate));

    public Task<VoucherDto> PostAsync(DeviceHost device, InvoiceRequest request) =>
        device.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(CompanyId, request));
}
