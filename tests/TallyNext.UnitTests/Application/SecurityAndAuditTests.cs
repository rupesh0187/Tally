using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Audit;
using TallyNext.Application.Common;
using TallyNext.Application.Companies;
using TallyNext.Application.Reports;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Audit;
using TallyNext.Contracts.Companies;
using TallyNext.Contracts.Reports;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.UnitTests.Support;

namespace TallyNext.UnitTests.Application;

public sealed class SecurityAndAuditTests
{
    private static readonly DateOnly May10 = new(2026, 5, 10);

    [Fact]
    public async Task A_user_cannot_read_or_write_another_users_company()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);

        host.Session.SignIn(new SignedInUser(Guid.CreateVersion7(), "intruder@example.test", "Intruder", true, DateTime.UtcNow));

        await Assert.ThrowsAsync<NotFoundException>(() => host.RunAsync<CompanyService, CompanyDto>(s => s.GetAsync(books.CompanyId)));
        await Assert.ThrowsAsync<NotFoundException>(() => host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, May10, May10)));
        await Assert.ThrowsAsync<NotFoundException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 1m), (books.Capital, DrCr.Credit, 1m)))));
        Assert.Empty(await host.RunAsync<CompanyService, IReadOnlyList<CompanySummaryDto>>(s => s.ListMineAsync()));
    }

    [Fact]
    public async Task Ledgers_of_another_company_cannot_be_used_in_a_voucher()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var a = await TestBooks.CreateAsync(host, "Company A");
        var b = await TestBooks.CreateAsync(host, "Company B");

        var ex = await Assert.ThrowsAsync<DomainException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(a.CompanyId,
            a.Voucher(VoucherBaseType.Journal, May10, (a.Rent, DrCr.Debit, 1m), (b.Capital, DrCr.Credit, 1m)))));
        Assert.Equal("voucher.ledger_not_found", ex.Code);
    }

    [Fact]
    public async Task Viewer_cannot_post_vouchers()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var viewer = Guid.CreateVersion7();
        await host.QueryAsync(async db =>
        {
            db.CompanyMemberships.Add(CompanyMembership.Create(books.CompanyId, viewer, CompanyRole.Viewer, "viewer@example.test", null));
            using (db.BeginRemoteApply())
            {
                return await db.SaveChangesAsync();
            }
        });

        host.Session.SignIn(new SignedInUser(viewer, "viewer@example.test", null, true, DateTime.UtcNow));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 1m), (books.Capital, DrCr.Credit, 1m)))));
        var tb = await host.RunAsync<ReportService, TrialBalanceReport>(s => s.TrialBalanceAsync(books.CompanyId, May10, May10));
        Assert.True(tb.IsBalanced);
    }

    [Fact]
    public async Task Audit_records_are_written_in_the_same_transaction_and_form_a_verifiable_chain()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var voucher = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 99m), (books.Capital, DrCr.Credit, 99m))));

        // A failed post writes neither the voucher nor its audit record.
        var auditCountBefore = await host.QueryAsync(db => db.AuditEntries.CountAsync());
        await Assert.ThrowsAsync<DomainException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 99m), (books.Capital, DrCr.Credit, 1m)))));
        Assert.Equal(auditCountBefore, await host.QueryAsync(db => db.AuditEntries.CountAsync()));

        var posted = await host.QueryAsync(db => db.AuditEntries.SingleAsync(a => a.EntityId == voucher.Id && a.Action == "voucher.posted"));
        Assert.Equal(host.UserId, posted.UserId);
        Assert.Equal(host.Session.DeviceId, posted.DeviceId);
        Assert.Contains("\"new\"", posted.ChangesJson, StringComparison.Ordinal);

        var ok = await host.RunAsync<AuditService, AuditVerificationResult>(s => s.VerifyAsync(books.CompanyId));
        Assert.True(ok.IsValid, ok.Problem);
        Assert.True(ok.EntriesChecked >= 7);

        // Tampering with any stored field breaks verification.
        await host.QueryAsync(db => db.Database.ExecuteSqlAsync($"UPDATE \"AuditEntries\" SET \"Summary\" = 'edited' WHERE \"Id\" = {posted.Id}"));
        var broken = await host.RunAsync<AuditService, AuditVerificationResult>(s => s.VerifyAsync(books.CompanyId));
        Assert.False(broken.IsValid);
        Assert.Equal(posted.ChainSequence, broken.FirstBrokenSequence);
    }

    [Fact]
    public async Task Deleting_an_audit_record_is_detected()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        await host.QueryAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM \"AuditEntries\" WHERE \"CompanyId\" = {books.CompanyId} AND \"ChainSequence\" = 2"));
        var result = await host.RunAsync<AuditService, AuditVerificationResult>(s => s.VerifyAsync(books.CompanyId));
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public async Task Duplicate_ledger_names_are_rejected_case_insensitively()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var groups = await host.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService, IReadOnlyList<TallyNext.Contracts.Accounts.AccountGroupDto>>(s => s.ListGroupsAsync(books.CompanyId));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService, TallyNext.Contracts.Accounts.LedgerDto>(s =>
            s.CreateLedgerAsync(books.CompanyId, new TallyNext.Contracts.Accounts.LedgerInput { Name = "  office   RENT ", GroupId = groups.Single(g => g.Name == "Indirect Expenses").Id })));
        Assert.Equal("ledger.duplicate_name", ex.Code);
    }
}
