using System.Net.Http.Json;
using System.Text.Json;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.SyncTests.Support;

namespace TallyNext.IntegrationTests;

/// <summary>Thin typed helpers over the HTTP API for tests.</summary>
public static class ApiClient
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, SyncJson.Options)!;
    }

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, SyncJson.Options);

    public sealed record Books(Guid CompanyId, Guid Bank, Guid Rent, Guid Capital, Guid PaymentType, Guid JournalType);

    public static async Task<Books> CreateBooksAsync(HttpClient client, string name = "API Test Co (Synthetic)")
    {
        var company = await (await client.PostJsonAsync("/api/companies", new CreateCompanyRequest { Name = name, StateCode = "07", BooksBeginningDate = new DateOnly(2026, 4, 1) })).ReadAsync<CompanyDto>();
        var groups = await (await client.GetAsync($"/api/companies/{company.Id}/groups")).ReadAsync<List<AccountGroupDto>>();
        async Task<Guid> Ledger(string ledger, string group, decimal opening, DrCr side) =>
            (await (await client.PostJsonAsync($"/api/companies/{company.Id}/ledgers", new LedgerInput
            {
                Name = ledger,
                GroupId = groups.Single(g => g.Name == group).Id,
                OpeningBalance = opening,
                OpeningSide = side,
            })).ReadAsync<LedgerDto>()).Id;

        var bank = await Ledger("API Bank", "Bank Accounts", 1_000m, DrCr.Debit);
        var capital = await Ledger("API Capital", "Capital Account", 1_000m, DrCr.Credit);
        var rent = await Ledger("API Rent", "Indirect Expenses", 0, DrCr.Debit);
        var types = await (await client.GetAsync($"/api/companies/{company.Id}/voucher-types")).ReadAsync<List<VoucherTypeDto>>();
        return new Books(company.Id, bank, rent, capital, types.Single(t => t.BaseType == VoucherBaseType.Payment).Id, types.Single(t => t.BaseType == VoucherBaseType.Journal).Id);
    }

    public static VoucherInput Payment(this Books books, decimal debit, decimal credit) => new()
    {
        VoucherTypeId = books.PaymentType,
        Date = new DateOnly(2026, 5, 5),
        Narration = "API test payment",
        Lines =
        [
            new VoucherLineInput { LedgerId = books.Rent, Side = DrCr.Debit, Amount = debit },
            new VoucherLineInput { LedgerId = books.Bank, Side = DrCr.Credit, Amount = credit },
        ],
    };

    public static Task<HttpClient> SignInAsync(this ApiServer server, string email) => server.SignedInClientAsync(email);
}
