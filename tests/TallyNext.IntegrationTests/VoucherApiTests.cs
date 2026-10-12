using System.Net;
using TallyNext.Contracts;
using TallyNext.Contracts.Audit;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.SyncTests.Support;

namespace TallyNext.IntegrationTests;

public sealed class VoucherApiTests
{
    private const string User = "accountant@vouchers.test";

    private static async Task<(ApiServer Server, HttpClient Client, ApiClient.Books Books)> SetupAsync()
    {
        var server = new ApiServer();
        await server.RegisterAsync(User);
        var client = await server.SignInAsync(User);
        return (server, client, await ApiClient.CreateBooksAsync(client));
    }

    [Fact]
    public async Task Balanced_voucher_is_accepted_and_unbalanced_or_imprecise_ones_are_rejected()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var url = $"/api/companies/{books.CompanyId}/vouchers";

        var created = await client.PostJsonAsync(url, books.Payment(250.75m, 250.75m));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var voucher = await created.ReadAsync<VoucherDto>();
        Assert.Equal("1", voucher.Number);
        Assert.Equal(250.75m, voucher.Total);

        var unbalanced = await client.PostJsonAsync(url, books.Payment(100m, 99m));
        Assert.Equal(HttpStatusCode.BadRequest, unbalanced.StatusCode);
        Assert.Equal("voucher.unbalanced", await unbalanced.ProblemCodeAsync());

        var imprecise = await client.PostJsonAsync(url, books.Payment(1.005m, 1.005m));
        Assert.Equal(HttpStatusCode.BadRequest, imprecise.StatusCode);
        Assert.Equal("money.precision", await imprecise.ProblemCodeAsync());

        var page = await (await client.GetAsync(url)).ReadAsync<PagedResult<VoucherListItem>>();
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Idempotency_key_makes_retries_safe()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var url = $"/api/companies/{books.CompanyId}/vouchers";
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = System.Net.Http.Json.JsonContent.Create(books.Payment(5m, 5m), options: TallyNext.Contracts.Sync.SyncJson.Options),
            };
            request.Headers.Add("Idempotency-Key", "client-retry-42");
            ids.Add((await (await client.SendAsync(request)).ReadAsync<VoucherDto>()).Id);
        }

        Assert.Single(ids.Distinct());
        Assert.Equal(1, (await (await client.GetAsync(url)).ReadAsync<PagedResult<VoucherListItem>>()).TotalCount);
    }

    [Fact]
    public async Task Simultaneous_saves_never_receive_duplicate_numbers()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var url = $"/api/companies/{books.CompanyId}/vouchers";

        var tasks = Enumerable.Range(0, 25).Select(_ => client.PostJsonAsync(url, books.Payment(1m, 1m))).ToList();
        var responses = await Task.WhenAll(tasks);
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await response.ReadAsync<VoucherDto>()).Number!);
        }

        Assert.Equal(25, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 25).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(), numbers.Order());
    }

    [Fact]
    public async Task Posted_vouchers_are_immutable_and_corrected_by_a_single_reversal()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var url = $"/api/companies/{books.CompanyId}/vouchers";
        var voucher = await (await client.PostJsonAsync(url, books.Payment(80m, 80m))).ReadAsync<VoucherDto>();

        var edit = await client.PutAsync($"{url}/{voucher.Id}", System.Net.Http.Json.JsonContent.Create(books.Payment(1m, 1m), options: TallyNext.Contracts.Sync.SyncJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal("voucher.posted_immutable", await edit.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"{url}/{voucher.Id}/cancel", null)).StatusCode);

        var reversal = await (await client.PostJsonAsync($"{url}/{voucher.Id}/reverse", new ReverseVoucherRequest { Date = new DateOnly(2026, 5, 31) })).ReadAsync<VoucherDto>();
        Assert.Equal(voucher.Id, reversal.ReversalOfVoucherId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostJsonAsync($"{url}/{voucher.Id}/reverse", new ReverseVoucherRequest { Date = new DateOnly(2026, 5, 31) })).StatusCode);

        var tb = await (await client.GetAsync($"/api/companies/{books.CompanyId}/reports/trial-balance?from=2026-04-01&to=2027-03-31")).ReadAsync<TrialBalanceReport>();
        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Rows, r => r.Id == books.Rent && (r.ClosingDebit != 0 || r.ClosingCredit != 0));
    }

    [Fact]
    public async Task Period_lock_blocks_postings_in_closed_periods()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        await (await client.PutAsync($"/api/companies/{books.CompanyId}/period-lock",
            System.Net.Http.Json.JsonContent.Create(new SetPeriodLockRequest { LockedThroughDate = new DateOnly(2026, 5, 31) }, options: TallyNext.Contracts.Sync.SyncJson.Options))).ReadAsync<CompanyDto>();

        var locked = await client.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(3m, 3m));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        Assert.Equal("voucher.period_locked", await locked.ProblemCodeAsync());
    }

    [Fact]
    public async Task Every_financial_change_is_audited_and_the_chain_verifies()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var voucher = await (await client.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(9m, 9m))).ReadAsync<VoucherDto>();

        var audit = await (await client.GetAsync($"/api/companies/{books.CompanyId}/audit?entityId={voucher.Id}")).ReadAsync<PagedResult<AuditEntryDto>>();
        var posted = Assert.Single(audit.Items);
        Assert.Equal("voucher.posted", posted.Action);
        Assert.Contains("API Rent", await (await client.GetAsync($"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var verification = await (await client.GetAsync($"/api/companies/{books.CompanyId}/audit/verify")).ReadAsync<AuditVerificationResult>();
        Assert.True(verification.IsValid, verification.Problem);
        Assert.True(verification.EntriesChecked >= 5);
    }

    [Fact]
    public async Task Errors_are_problem_details_without_internal_details()
    {
        var (server, client, books) = await SetupAsync();
        await using var _ = server;
        var response = await client.GetAsync($"/api/companies/{books.CompanyId}/vouchers/{Guid.CreateVersion7()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("voucher.not_found", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);

        var malformed = await client.PostAsync($"/api/companies/{books.CompanyId}/vouchers", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }
}
