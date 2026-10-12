using Microsoft.AspNetCore.Mvc;
using TallyNext.Application.Accounts;
using TallyNext.Application.Audit;
using TallyNext.Application.Companies;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Audit;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Vouchers;

namespace TallyNext.Api.Endpoints;

/// <summary>
/// Company-scoped REST endpoints. Authorization (membership + role) is enforced inside every use case, so these
/// handlers stay thin and cannot accidentally skip it.
/// </summary>
internal static class AccountingEndpoints
{
    public static void MapAccountingEndpoints(this IEndpointRouteBuilder app)
    {
        var reference = app.MapGroup("/api/reference").WithTags("Reference data");
        reference.MapGet("/states", () => TypedResults.Ok(CompanyService.States));
        reference.MapGet("/currencies", () => TypedResults.Ok(CompanyService.SupportedCurrencies));

        MapCompanies(app.MapGroup("/api/companies").WithTags("Companies"));
        var company = app.MapGroup("/api/companies/{companyId:guid}");
        MapMasters(company);
        MapVouchers(company.MapGroup("/vouchers").WithTags("Vouchers"));
        MapReports(company.MapGroup("/reports").WithTags("Reports"));
        MapAudit(company.MapGroup("/audit").WithTags("Audit"));
    }

    private static void MapCompanies(RouteGroupBuilder g)
    {
        g.MapGet("/", async (CompanyService s, CancellationToken ct) => TypedResults.Ok(await s.ListMineAsync(ct)));
        g.MapPost("/", async (CreateCompanyRequest r, CompanyService s, CancellationToken ct) =>
        {
            var company = await s.CreateAsync(r, ct);
            return TypedResults.Created($"/api/companies/{company.Id}", company);
        });
        g.MapGet("/{companyId:guid}", async (Guid companyId, CompanyService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(companyId, ct)));
        g.MapPut("/{companyId:guid}", async (Guid companyId, UpdateCompanyRequest r, CompanyService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(companyId, r, ct)));
        g.MapPut("/{companyId:guid}/period-lock", async (Guid companyId, SetPeriodLockRequest r, CompanyService s, CancellationToken ct) => TypedResults.Ok(await s.SetPeriodLockAsync(companyId, r, ct)));

        g.MapGet("/{companyId:guid}/members", async (Guid companyId, MembershipService s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(companyId, ct)));
        g.MapPost("/{companyId:guid}/members", async (Guid companyId, AddMemberRequest r, MembershipService s, CancellationToken ct) => TypedResults.Ok(await s.AddAsync(companyId, r, ct)));
        g.MapPut("/{companyId:guid}/members/{membershipId:guid}/role", async (Guid companyId, Guid membershipId, ChangeMemberRoleRequest r, MembershipService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ChangeRoleAsync(companyId, membershipId, r.Role, ct)));
        g.MapDelete("/{companyId:guid}/members/{membershipId:guid}", async (Guid companyId, Guid membershipId, MembershipService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(companyId, membershipId, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapMasters(RouteGroupBuilder company)
    {
        var groups = company.MapGroup("/groups").WithTags("Account groups");
        groups.MapGet("/", async (Guid companyId, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.ListGroupsAsync(companyId, ct)));
        groups.MapPost("/", async (Guid companyId, AccountGroupInput r, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.CreateGroupAsync(companyId, r, ct)));
        groups.MapPut("/{groupId:guid}", async (Guid companyId, Guid groupId, AccountGroupInput r, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateGroupAsync(companyId, groupId, r, ct)));
        groups.MapDelete("/{groupId:guid}", async (Guid companyId, Guid groupId, ChartOfAccountsService s, CancellationToken ct) =>
        {
            await s.DeleteGroupAsync(companyId, groupId, ct);
            return TypedResults.NoContent();
        });

        var ledgers = company.MapGroup("/ledgers").WithTags("Ledgers");
        ledgers.MapGet("/", async (Guid companyId, string? search, Guid? groupId, bool? cashOrBankOnly, int? page, int? pageSize, ChartOfAccountsService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListLedgersAsync(companyId, new LedgerQuery
            {
                Search = search,
                GroupId = groupId,
                CashOrBankOnly = cashOrBankOnly ?? false,
                Page = page ?? 1,
                PageSize = pageSize ?? Contracts.Paging.DefaultPageSize,
            }, ct)));
        ledgers.MapGet("/lookup", async (Guid companyId, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.LookupLedgersAsync(companyId, ct)));
        ledgers.MapGet("/{ledgerId:guid}", async (Guid companyId, Guid ledgerId, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.GetLedgerAsync(companyId, ledgerId, ct)));
        ledgers.MapPost("/", async (Guid companyId, LedgerInput r, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.CreateLedgerAsync(companyId, r, ct)));
        ledgers.MapPut("/{ledgerId:guid}", async (Guid companyId, Guid ledgerId, LedgerInput r, ChartOfAccountsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateLedgerAsync(companyId, ledgerId, r, ct)));
        ledgers.MapDelete("/{ledgerId:guid}", async (Guid companyId, Guid ledgerId, ChartOfAccountsService s, CancellationToken ct) =>
        {
            await s.DeleteLedgerAsync(companyId, ledgerId, ct);
            return TypedResults.NoContent();
        });

        company.MapGet("/voucher-types", async (Guid companyId, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.ListTypesAsync(companyId, ct)))
            .WithTags("Vouchers");
    }

    private static void MapVouchers(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid companyId, DateOnly? from, DateOnly? to, Guid? voucherTypeId, VoucherStatus? status, string? search, int? page, int? pageSize, VoucherService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(companyId, new VoucherQuery
            {
                From = from,
                To = to,
                VoucherTypeId = voucherTypeId,
                Status = status,
                Search = search,
                Page = page ?? 1,
                PageSize = pageSize ?? Contracts.Paging.DefaultPageSize,
            }, ct)));
        g.MapPost("/", async (Guid companyId, VoucherInput r, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, VoucherService s, CancellationToken ct) =>
            {
                r.IdempotencyKey ??= idempotencyKey;
                var voucher = await s.CreateAsync(companyId, r, ct);
                return TypedResults.Created($"/api/companies/{companyId}/vouchers/{voucher.Id}", voucher);
            })
            .WithSummary("Create a voucher (posted by default). Safe to retry with the same id or Idempotency-Key.");
        g.MapGet("/{voucherId:guid}", async (Guid companyId, Guid voucherId, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(companyId, voucherId, ct)));
        g.MapPut("/{voucherId:guid}", async (Guid companyId, Guid voucherId, VoucherInput r, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateDraftAsync(companyId, voucherId, r, ct)))
            .WithSummary("Update a draft (posted vouchers are immutable).");
        g.MapPost("/{voucherId:guid}/post", async (Guid companyId, Guid voucherId, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.PostAsync(companyId, voucherId, ct)));
        g.MapPost("/{voucherId:guid}/cancel", async (Guid companyId, Guid voucherId, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.CancelDraftAsync(companyId, voucherId, ct)));
        g.MapPost("/{voucherId:guid}/reverse", async (Guid companyId, Guid voucherId, ReverseVoucherRequest r, VoucherService s, CancellationToken ct) => TypedResults.Ok(await s.ReverseAsync(companyId, voucherId, r, ct)))
            .WithSummary("Post the mirror image of a posted voucher (the only way to correct one).");
    }

    private static void MapReports(RouteGroupBuilder g)
    {
        g.MapGet("/day-book", async (Guid companyId, DateOnly from, DateOnly to, int? page, int? pageSize, bool? includeDrafts, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.DayBookAsync(companyId, from, to, page ?? 1, pageSize ?? Contracts.Paging.DefaultPageSize, includeDrafts ?? false, ct)));
        g.MapGet("/ledger/{ledgerId:guid}", async (Guid companyId, Guid ledgerId, DateOnly from, DateOnly to, int? page, int? pageSize, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.LedgerAsync(companyId, ledgerId, from, to, page ?? 1, pageSize ?? Contracts.Paging.DefaultPageSize, ct)));
        g.MapGet("/trial-balance", async (Guid companyId, DateOnly from, DateOnly to, bool? includeZero, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.TrialBalanceAsync(companyId, from, to, includeZero ?? false, ct)));
        g.MapGet("/profit-and-loss", async (Guid companyId, DateOnly from, DateOnly to, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ProfitAndLossAsync(companyId, from, to, ct)));
        g.MapGet("/balance-sheet", async (Guid companyId, DateOnly asAt, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.BalanceSheetAsync(companyId, asAt, ct)));
        g.MapGet("/cash-bank", async (Guid companyId, DateOnly from, DateOnly to, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.CashAndBankSummaryAsync(companyId, from, to, ct)));
    }

    private static void MapAudit(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid companyId, string? entityType, Guid? entityId, string? action, DateTime? fromUtc, DateTime? toUtc, int? page, int? pageSize, AuditService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(companyId, new AuditQuery
            {
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Page = page ?? 1,
                PageSize = pageSize ?? Contracts.Paging.DefaultPageSize,
            }, ct)));
        g.MapGet("/verify", async (Guid companyId, AuditService s, CancellationToken ct) => TypedResults.Ok(await s.VerifyAsync(companyId, ct)))
            .WithSummary("Recompute the audit hash chain and report the first broken record, if any.");
    }
}
