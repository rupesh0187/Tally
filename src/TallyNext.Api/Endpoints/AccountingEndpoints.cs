using Microsoft.AspNetCore.Mvc;
using TallyNext.Application.Accounts;
using TallyNext.Application.Audit;
using TallyNext.Application.Bills;
using TallyNext.Application.Companies;
using TallyNext.Application.Inventory;
using TallyNext.Application.Reports;
using TallyNext.Application.Tax;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Accounts;
using TallyNext.Contracts.Audit;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Inventory;
using TallyNext.Contracts.Reports;
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
        MapInventory(company.MapGroup("/inventory").WithTags("Inventory"));
        MapGst(company.MapGroup("/gst").WithTags("GST"));
        MapVouchers(company.MapGroup("/vouchers").WithTags("Vouchers"));
        MapInvoices(company.MapGroup("/invoices").WithTags("Invoices"));
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
        ledgers.MapGet("/{ledgerId:guid}/pending-bills", async (Guid companyId, Guid ledgerId, DateOnly? asAt, BillWiseService s, CancellationToken ct) =>
                TypedResults.Ok(await s.PendingBillsAsync(companyId, ledgerId, asAt, ct)))
            .WithSummary("Bills of a bill-wise ledger with a pending amount (to choose what a payment or receipt settles).");
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

    private static void MapInventory(RouteGroupBuilder g)
    {
        g.MapPost("/defaults", async (Guid companyId, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.EnsureDefaultsAsync(companyId, ct)))
            .WithSummary("Create the default unit (Nos) and Main Location godown when missing (companies created before inventory).");
        g.MapGet("/units", async (Guid companyId, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.ListUnitsAsync(companyId, ct)));
        g.MapPost("/units", async (Guid companyId, UnitInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveUnitAsync(companyId, null, r, ct)));
        g.MapPut("/units/{unitId:guid}", async (Guid companyId, Guid unitId, UnitInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveUnitAsync(companyId, unitId, r, ct)));

        g.MapGet("/godowns", async (Guid companyId, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.ListGodownsAsync(companyId, ct)));
        g.MapPost("/godowns", async (Guid companyId, GodownInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveGodownAsync(companyId, null, r, ct)));
        g.MapPut("/godowns/{godownId:guid}", async (Guid companyId, Guid godownId, GodownInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveGodownAsync(companyId, godownId, r, ct)));

        g.MapGet("/items", async (Guid companyId, string? search, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.ListItemsAsync(companyId, search, ct)));
        g.MapGet("/items/lookup", async (Guid companyId, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.LookupItemsAsync(companyId, ct)));
        g.MapGet("/items/{itemId:guid}", async (Guid companyId, Guid itemId, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.GetItemAsync(companyId, itemId, ct)));
        g.MapPost("/items", async (Guid companyId, StockItemInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveItemAsync(companyId, null, r, ct)));
        g.MapPut("/items/{itemId:guid}", async (Guid companyId, Guid itemId, StockItemInput r, InventoryService s, CancellationToken ct) => TypedResults.Ok(await s.SaveItemAsync(companyId, itemId, r, ct)));
        g.MapDelete("/items/{itemId:guid}", async (Guid companyId, Guid itemId, InventoryService s, CancellationToken ct) =>
        {
            await s.DeleteItemAsync(companyId, itemId, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapGst(RouteGroupBuilder g)
    {
        g.MapPut("/registration", async (Guid companyId, SetGstRegistrationRequest r, GstService s, CancellationToken ct) => TypedResults.Ok(await s.SetRegistrationAsync(companyId, r, ct)))
            .WithSummary("Set the company's GST registration (not registered / regular / composition).");
        g.MapPost("/tax-ledgers", async (Guid companyId, GstService s, CancellationToken ct) =>
            {
                await s.EnsureTaxLedgersAsync(companyId, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Create any missing CGST/SGST/UTGST/IGST/Cess and Round Off ledgers.");
    }

    private static void MapInvoices(RouteGroupBuilder g)
    {
        g.MapPost("/preview", async (Guid companyId, InvoiceRequest r, InvoiceService s, CancellationToken ct) => TypedResults.Ok(await s.PreviewAsync(companyId, r, ct)))
            .WithSummary("Compute tax and totals for an invoice without saving it.");
        g.MapPost("/", async (Guid companyId, InvoiceRequest r, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, InvoiceService s, CancellationToken ct) =>
            {
                r.IdempotencyKey ??= idempotencyKey;
                var voucher = await s.CreateAsync(companyId, r, ct);
                return TypedResults.Created($"/api/companies/{companyId}/vouchers/{voucher.Id}", voucher);
            })
            .WithSummary("Create a sales/purchase invoice or credit/debit note with item lines. Safe to retry with the same id or Idempotency-Key.");
        g.MapPut("/{voucherId:guid}", async (Guid companyId, Guid voucherId, InvoiceRequest r, InvoiceService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateDraftAsync(companyId, voucherId, r, ct)))
            .WithSummary("Update a draft invoice.");
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
        g.MapGet("/stock-summary", async (Guid companyId, DateOnly from, DateOnly to, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.StockSummaryAsync(companyId, from, to, ct)));
        g.MapGet("/outstanding", async (Guid companyId, OutstandingKind kind, DateOnly asAt, Guid? ledgerId, BillWiseService s, CancellationToken ct) =>
            TypedResults.Ok(await s.OutstandingAsync(companyId, kind, asAt, ledgerId, ct)))
            .WithSummary("Pending bills with ageing and on-account amounts (kind = Receivables or Payables).");
        g.MapGet("/gst-summary", async (Guid companyId, DateOnly from, DateOnly to, ReportService s, CancellationToken ct) =>
            TypedResults.Ok(await s.GstSummaryAsync(companyId, from, to, ct)))
            .WithSummary("Outward and inward supplies by rate with tax. An aid for returns, not a return.");
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
