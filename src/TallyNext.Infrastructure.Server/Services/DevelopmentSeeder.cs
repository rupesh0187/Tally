using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TallyNext.Application.Accounts;
using TallyNext.Application.Companies;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Services;

/// <summary>
/// Development-only seed: one user and one company populated with clearly synthetic data. Runs only when the host
/// environment is Development and <c>Seed:DevUserEmail</c>/<c>Seed:DevUserPassword</c> are configured. Never holds
/// real credentials or business data.
/// </summary>
public sealed class DevelopmentSeeder(
    UserManager<AppUser> users,
    ServerExecutionContext context,
    CompanyService companies,
    ChartOfAccountsService chart,
    VoucherService vouchers,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<DevelopmentSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var email = configuration["Seed:DevUserEmail"];
        var password = configuration["Seed:DevUserPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new AppUser
        {
            Id = Ids.New(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Development Owner",
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogWarning("Development user was not created: {Errors}", string.Join("; ", result.Errors.Select(e => e.Code)));
            return;
        }

        context.Use(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TallyClaims.Subject, user.Id.ToString()), new Claim(TallyClaims.Email, email), new Claim(TallyClaims.Name, user.DisplayName)],
            authenticationType: "seed")));

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var fyStart = FinancialYear.Containing(today, 4).Start;
        var company = await companies.CreateAsync(new CreateCompanyRequest
        {
            Name = "Demo Traders (Synthetic Data)",
            StateCode = "27",
            City = "Pune",
            BooksBeginningDate = fyStart,
        }, cancellationToken);

        var groups = await chart.ListGroupsAsync(company.Id, cancellationToken);
        Guid Group(string name) => groups.Single(g => g.Name == name).Id;
        async Task<Guid> Ledger(string name, string group, decimal opening = 0, DrCr side = DrCr.Debit) =>
            (await chart.CreateLedgerAsync(company.Id, new LedgerInput { Name = name, GroupId = Group(group), OpeningBalance = opening, OpeningSide = side }, cancellationToken)).Id;

        var bank = await Ledger("Demo Bank Current A/c", "Bank Accounts", 250_000m, DrCr.Debit);
        await Ledger("Proprietor Capital", "Capital Account", 300_000m, DrCr.Credit);
        var cash = (await chart.LookupLedgersAsync(company.Id, cancellationToken)).Single(l => l.Name == "Cash").Id;
        await chart.UpdateLedgerAsync(company.Id, cash, new LedgerInput { Name = "Cash", GroupId = Group("Cash-in-Hand"), OpeningBalance = 50_000m, OpeningSide = DrCr.Debit }, cancellationToken);
        var sales = await Ledger("Sales (Synthetic)", "Sales Accounts");
        var purchases = await Ledger("Purchases (Synthetic)", "Purchase Accounts");
        var rent = await Ledger("Office Rent", "Indirect Expenses");
        var customer = await Ledger("Sample Customer A", "Sundry Debtors");
        var supplier = await Ledger("Sample Supplier B", "Sundry Creditors");

        var types = (await vouchers.ListTypesAsync(company.Id, cancellationToken)).ToDictionary(t => t.BaseType, t => t.Id);
        async Task Post(VoucherBaseType type, int day, string narration, params (Guid Ledger, DrCr Side, decimal Amount)[] lines)
        {
            var date = fyStart.AddDays(day);
            if (date > today)
            {
                date = today;
            }

            await vouchers.CreateAsync(company.Id, new VoucherInput
            {
                VoucherTypeId = types[type],
                Date = date,
                Narration = narration,
                Post = true,
                Lines = lines.Select(l => new VoucherLineInput { LedgerId = l.Ledger, Side = l.Side, Amount = l.Amount }).ToList(),
            }, cancellationToken);
        }

        await Post(VoucherBaseType.Sales, 2, "Synthetic sale", (customer, DrCr.Debit, 59_000m), (sales, DrCr.Credit, 59_000m));
        await Post(VoucherBaseType.Purchase, 3, "Synthetic purchase", (purchases, DrCr.Debit, 30_000m), (supplier, DrCr.Credit, 30_000m));
        await Post(VoucherBaseType.Receipt, 10, "Part payment received", (bank, DrCr.Debit, 40_000m), (customer, DrCr.Credit, 40_000m));
        await Post(VoucherBaseType.Payment, 12, "Office rent", (rent, DrCr.Debit, 15_000m), (bank, DrCr.Credit, 15_000m));
        await Post(VoucherBaseType.Contra, 15, "Cash withdrawn", (cash, DrCr.Debit, 10_000m), (bank, DrCr.Credit, 10_000m));
        await Post(VoucherBaseType.Payment, 20, "Supplier paid", (supplier, DrCr.Debit, 30_000m), (bank, DrCr.Credit, 30_000m));

        logger.LogInformation("Development data seeded for {Email} (company {CompanyId})", email, company.Id);
    }
}
