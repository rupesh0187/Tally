using System.Net;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Companies;
using TallyNext.SyncTests.Support;

namespace TallyNext.IntegrationTests;

/// <summary>Tenant isolation and role enforcement through the public API.</summary>
public sealed class CompanyIsolationTests
{
    private const string Alice = "alice@isolation.test";
    private const string Bob = "bob@isolation.test";

    [Fact]
    public async Task Another_users_company_is_invisible_on_every_endpoint()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Alice);
        await server.RegisterAsync(Bob);
        var alice = await server.SignInAsync(Alice);
        var books = await ApiClient.CreateBooksAsync(alice);
        var voucher = await (await alice.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(10m, 10m))).ReadAsync<VoucherDto>();

        var bob = await server.SignInAsync(Bob);
        string[] reads =
        [
            $"/api/companies/{books.CompanyId}",
            $"/api/companies/{books.CompanyId}/groups",
            $"/api/companies/{books.CompanyId}/ledgers",
            $"/api/companies/{books.CompanyId}/ledgers/{books.Bank}",
            $"/api/companies/{books.CompanyId}/vouchers",
            $"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}",
            $"/api/companies/{books.CompanyId}/reports/trial-balance?from=2026-04-01&to=2027-03-31",
            $"/api/companies/{books.CompanyId}/reports/ledger/{books.Bank}?from=2026-04-01&to=2027-03-31",
            $"/api/companies/{books.CompanyId}/audit",
            $"/api/companies/{books.CompanyId}/members",
            $"/api/sync/pull?companyId={books.CompanyId}",
        ];
        foreach (var url in reads)
        {
            var response = await bob.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} returned {(int)response.StatusCode}");
        }

        var write = await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(1m, 1m));
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        var reverse = await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}/reverse", new ReverseVoucherRequest { Date = new DateOnly(2026, 5, 6) });
        Assert.Equal(HttpStatusCode.NotFound, reverse.StatusCode);
        Assert.Empty(await (await bob.GetAsync("/api/companies")).ReadAsync<List<CompanySummaryDto>>());

        // Bob's own company cannot reference Alice's ledgers.
        var bobBooks = await ApiClient.CreateBooksAsync(bob, "Bob Co (Synthetic)");
        var cross = bobBooks with { Bank = books.Bank };
        var crossWrite = await bob.PostJsonAsync($"/api/companies/{bobBooks.CompanyId}/vouchers", cross.Payment(1m, 1m));
        Assert.Equal(HttpStatusCode.BadRequest, crossWrite.StatusCode);
        Assert.Equal("voucher.ledger_not_found", await crossWrite.ProblemCodeAsync());
    }

    [Fact]
    public async Task Roles_limit_what_members_can_do_and_revocation_removes_access()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Alice);
        await server.RegisterAsync(Bob);
        var alice = await server.SignInAsync(Alice);
        var books = await ApiClient.CreateBooksAsync(alice);
        var voucher = await (await alice.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(10m, 10m))).ReadAsync<VoucherDto>();

        var membership = await (await alice.PostJsonAsync($"/api/companies/{books.CompanyId}/members", new AddMemberRequest { Email = Bob, Role = CompanyRole.Viewer })).ReadAsync<MemberDto>();
        var bob = await server.SignInAsync(Bob);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/companies/{books.CompanyId}/reports/trial-balance?from=2026-04-01&to=2027-03-31")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(1m, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/companies/{books.CompanyId}/audit")).StatusCode);

        await (await alice.PutAsync($"/api/companies/{books.CompanyId}/members/{membership.MembershipId}/role",
            System.Net.Http.Json.JsonContent.Create(new ChangeMemberRoleRequest { Role = CompanyRole.DataEntry }, options: TallyNext.Contracts.Sync.SyncJson.Options))).ReadAsync<MemberDto>();
        Assert.Equal(HttpStatusCode.Created, (await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(1m, 1m))).StatusCode);
        var reverse = await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers/{voucher.Id}/reverse", new ReverseVoucherRequest { Date = new DateOnly(2026, 5, 6) });
        Assert.Equal(HttpStatusCode.Forbidden, reverse.StatusCode);

        // The last owner cannot be demoted.
        var members = await (await alice.GetAsync($"/api/companies/{books.CompanyId}/members")).ReadAsync<List<MemberDto>>();
        var owner = members.Single(m => m.Role == CompanyRole.Owner);
        var demote = await alice.PutAsync($"/api/companies/{books.CompanyId}/members/{owner.MembershipId}/role",
            System.Net.Http.Json.JsonContent.Create(new ChangeMemberRoleRequest { Role = CompanyRole.Viewer }, options: TallyNext.Contracts.Sync.SyncJson.Options));
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/companies/{books.CompanyId}/members/{membership.MembershipId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/companies/{books.CompanyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostJsonAsync($"/api/companies/{books.CompanyId}/vouchers", books.Payment(1m, 1m))).StatusCode);
    }
}
