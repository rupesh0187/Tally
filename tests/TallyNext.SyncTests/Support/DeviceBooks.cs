using TallyNext.Application.Accounts;
using TallyNext.Application.Companies;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;

namespace TallyNext.SyncTests.Support;

/// <summary>Synthetic company created through the application services of one device.</summary>
public sealed record DeviceBooks(Guid CompanyId, Guid Cash, Guid Bank, Guid Rent, Guid Customer, Guid Capital, IReadOnlyDictionary<VoucherBaseType, Guid> Types)
{
    public static readonly DateOnly BooksBeginning = new(2026, 4, 1);

    public static async Task<DeviceBooks> CreateAsync(DeviceHost device, string name = "Sync Test Traders (Synthetic)")
    {
        var company = await device.RunAsync<CompanyService, CompanyDto>(s => s.CreateAsync(new CreateCompanyRequest
        {
            Name = name,
            StateCode = "29",
            BooksBeginningDate = BooksBeginning,
        }));
        return await LoadAsync(device, company.Id, create: true);
    }

    /// <summary>Resolves the ledger ids of a company that was synchronized to <paramref name="device"/>.</summary>
    public static Task<DeviceBooks> OpenAsync(DeviceHost device, Guid companyId) => LoadAsync(device, companyId, create: false);

    private static async Task<DeviceBooks> LoadAsync(DeviceHost device, Guid companyId, bool create)
    {
        var groups = await device.RunAsync<ChartOfAccountsService, IReadOnlyList<AccountGroupDto>>(s => s.ListGroupsAsync(companyId));
        Guid Group(string n) => groups.Single(g => g.Name == n).Id;

        async Task<Guid> Ledger(string ledgerName, string group, decimal opening = 0, DrCr side = DrCr.Debit)
        {
            var existing = (await device.RunAsync<ChartOfAccountsService, IReadOnlyList<LedgerLookupItem>>(s => s.LookupLedgersAsync(companyId)))
                .SingleOrDefault(l => l.Name == ledgerName);
            if (existing is not null || !create)
            {
                return existing?.Id ?? throw new InvalidOperationException($"Ledger {ledgerName} not found on device.");
            }

            return (await device.RunAsync<ChartOfAccountsService, LedgerDto>(s => s.CreateLedgerAsync(companyId, new LedgerInput
            {
                Name = ledgerName,
                GroupId = Group(group),
                OpeningBalance = opening,
                OpeningSide = side,
            }))).Id;
        }

        var bank = await Ledger("Sync Bank A/c", "Bank Accounts", 10_000m, DrCr.Debit);
        var capital = await Ledger("Sync Capital", "Capital Account", 10_000m, DrCr.Credit);
        var rent = await Ledger("Sync Rent", "Indirect Expenses");
        var customer = await Ledger("Sync Customer", "Sundry Debtors");
        var cash = await Ledger("Cash", "Cash-in-Hand");
        var types = await device.RunAsync<VoucherService, IReadOnlyList<VoucherTypeDto>>(s => s.ListTypesAsync(companyId));
        return new DeviceBooks(companyId, cash, bank, rent, customer, capital, types.Where(t => t.IsSystem).ToDictionary(t => t.BaseType, t => t.Id));
    }

    public Task<VoucherDto> PostPaymentAsync(DeviceHost device, decimal amount, DateOnly? date = null, Guid? id = null) =>
        device.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(CompanyId, new VoucherInput
        {
            Id = id,
            VoucherTypeId = Types[VoucherBaseType.Payment],
            Date = date ?? new DateOnly(2026, 6, 1),
            Narration = "Synthetic rent payment",
            Post = true,
            Lines =
            [
                new VoucherLineInput { LedgerId = Rent, Side = DrCr.Debit, Amount = amount },
                new VoucherLineInput { LedgerId = Bank, Side = DrCr.Credit, Amount = amount },
            ],
        }));

    public Task<TrialBalanceReport> TrialBalanceAsync(DeviceHost device) =>
        device.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(CompanyId, BooksBeginning, new DateOnly(2027, 3, 31)));
}
