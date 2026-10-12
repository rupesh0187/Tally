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
using TallyNext.Domain.Tax;
using TallyNext.Domain.Vouchers;

namespace TallyNext.UnitTests.Support;

/// <summary>
/// A GST-registered synthetic company (Maharashtra) with an 18% stock item, parties in and outside the State, and
/// a purchase ledger. GSTINs are synthetic: valid format and checksum, not real registrations.
/// </summary>
public sealed record GstBooks(
    TestBooks Books,
    Guid Purchases,
    Guid LocalCustomer,
    Guid OutOfStateCustomer,
    Guid Supplier,
    Guid Widget,
    Guid MainGodown)
{
    public Guid CompanyId => Books.CompanyId;

    public static string SyntheticGstin(string stateCode, string pan)
    {
        var first14 = stateCode + pan + "1Z";
        return first14 + Gstin.ComputeCheckCharacter(first14);
    }

    public static async Task<GstBooks> CreateAsync(LocalTestHost host, ValuationMethod valuation = ValuationMethod.WeightedAverage)
    {
        var books = await TestBooks.CreateAsync(host);
        var companyId = books.CompanyId;
        var company = await host.RunAsync<CompanyService, CompanyDto>(s => s.GetAsync(companyId));
        await host.RunAsync<CompanyService, CompanyDto>(s => s.UpdateAsync(companyId, new UpdateCompanyRequest
        {
            Name = company.Name,
            StateCode = "27",
            Gstin = SyntheticGstin("27", "AAACS1234F"),
            ExpectedVersion = company.Version,
        }));
        await host.RunAsync<GstService, CompanyDto>(s => s.SetRegistrationAsync(companyId, new SetGstRegistrationRequest { Registration = GstRegistrationType.Regular }));

        var groups = await host.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(companyId));
        Guid GroupNamed(string n) => groups.Single(g => g.Name == n).Id;
        async Task<Guid> Ledger(LedgerInput input) => (await host.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(companyId, input))).Id;

        var purchases = await Ledger(new LedgerInput { Name = "Purchases - Goods", GroupId = GroupNamed("Purchase Accounts") });
        var localCustomer = await Ledger(new LedgerInput
        {
            Name = "Local Customer",
            GroupId = GroupNamed("Sundry Debtors"),
            GstRegistrationType = GstRegistrationType.Regular,
            StateCode = "27",
            Gstin = SyntheticGstin("27", "AAACL5678K"),
        });
        var outOfState = await Ledger(new LedgerInput
        {
            Name = "Karnataka Consumer",
            GroupId = GroupNamed("Sundry Debtors"),
            GstRegistrationType = GstRegistrationType.Consumer,
            StateCode = "29",
        });
        var supplier = await Ledger(new LedgerInput
        {
            Name = "Synthetic Supplier",
            GroupId = GroupNamed("Sundry Creditors"),
            GstRegistrationType = GstRegistrationType.Regular,
            StateCode = "27",
            Gstin = SyntheticGstin("27", "AABCT9012M"),
        });

        var units = await host.RunAsync<InventoryService, IReadOnlyList<UnitDto>>(s => s.ListUnitsAsync(companyId));
        var godowns = await host.RunAsync<InventoryService, IReadOnlyList<GodownDto>>(s => s.ListGodownsAsync(companyId));
        var main = godowns.Single(g => g.IsSystem).Id;
        var widget = await host.RunAsync<InventoryService, StockItemDto>(s => s.SaveItemAsync(companyId, null, new StockItemInput
        {
            Name = "Widget",
            UnitId = units.Single(u => u.Symbol == "Nos").Id,
            ValuationMethod = valuation,
            GstRates = [new GstRateInput { EffectiveFrom = TestBooks.BooksBeginning, IgstPercent = 18m, HsnSac = "8471" }],
            OpeningQuantity = 10,
            OpeningValue = 1_000m,
            OpeningGodownId = main,
        }));

        return new GstBooks(books, purchases, localCustomer, outOfState, supplier, widget.Id, main);
    }

    public InvoiceRequest Invoice(VoucherBaseType type, DateOnly date, Guid party, params (decimal Quantity, decimal Rate)[] lines) => new()
    {
        VoucherTypeId = Books.Types[type],
        Date = date,
        PartyLedgerId = party,
        Narration = $"{type} invoice test",
        RoundOff = false,
        Post = true,
        Lines = lines.Select(l => new InvoiceRequestLine
        {
            Kind = InvoiceLineKind.Stock,
            StockItemId = Widget,
            Quantity = l.Quantity,
            Rate = l.Rate,
            LedgerId = type is VoucherBaseType.Purchase or VoucherBaseType.DebitNote ? Purchases : Books.Sales,
        }).ToList(),
    };

    public Task<VoucherDto> PostAsync(LocalTestHost host, InvoiceRequest request) =>
        host.RunAsync<InvoiceService, VoucherDto>(s => s.CreateAsync(CompanyId, request));
}
