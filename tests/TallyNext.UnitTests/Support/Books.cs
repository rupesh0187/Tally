using TallyNext.Application.Accounts;
using TallyNext.Application.Companies;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.UnitTests.Support;

/// <summary>Synthetic test company with a few ledgers. No real business data.</summary>
public sealed record TestBooks(
    Guid CompanyId,
    Guid Cash,
    Guid Bank,
    Guid Capital,
    Guid Sales,
    Guid Rent,
    Guid Customer,
    IReadOnlyDictionary<VoucherBaseType, Guid> Types)
{
    public static readonly DateOnly BooksBeginning = new(2026, 4, 1);

    public static async Task<TestBooks> CreateAsync(LocalTestHost host, string name = "Synthetic Traders Pvt Ltd")
    {
        var company = await host.RunAsync<CompanyService, CompanyDto>(s => s.CreateAsync(new CreateCompanyRequest
        {
            Name = name,
            StateCode = "27",
            BooksBeginningDate = BooksBeginning,
            FinancialYearStartMonth = 4,
        }));

        var groups = await host.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(company.Id));
        Guid GroupNamed(string n) => groups.Single(g => g.Name == n).Id;
        var ledgers = await host.RunAsync<ChartOfAccountsService, IReadOnlyList<LedgerLookupItem>>(s => s.LookupLedgersAsync(company.Id));

        async Task<Guid> Ledger(string ledgerName, string group, decimal opening = 0, DrCr side = DrCr.Debit) =>
            (await host.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(company.Id, new LedgerInput
            {
                Name = ledgerName,
                GroupId = GroupNamed(group),
                OpeningBalance = opening,
                OpeningSide = side,
            }))).Id;

        var bank = await Ledger("Synthetic Bank A/c", "Bank Accounts", 50_000m, DrCr.Debit);
        var capital = await Ledger("Owner Capital", "Capital Account", 50_000m, DrCr.Credit);
        var sales = await Ledger("Sales - Local", "Sales Accounts");
        var rent = await Ledger("Office Rent", "Indirect Expenses");
        var customer = await Ledger("Customer One", "Sundry Debtors");

        var types = await host.RunAsync<VoucherService, IReadOnlyList<VoucherTypeDto>>(s => s.ListTypesAsync(company.Id));
        return new TestBooks(
            company.Id,
            ledgers.Single(l => l.Name == "Cash").Id,
            bank,
            capital,
            sales,
            rent,
            customer,
            types.Where(t => t.IsSystem).ToDictionary(t => t.BaseType, t => t.Id));
    }

    public VoucherInput Voucher(VoucherBaseType type, DateOnly date, params (Guid Ledger, DrCr Side, decimal Amount)[] lines) => new()
    {
        VoucherTypeId = Types[type],
        Date = date,
        Narration = $"{type} test",
        Post = true,
        Lines = lines.Select(l => new VoucherLineInput { LedgerId = l.Ledger, Side = l.Side, Amount = l.Amount }).ToList(),
    };
}
