using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Common;
using TallyNext.Application.Vouchers;
using TallyNext.Contracts.Sync;
using TallyNext.Contracts.Vouchers;
using TallyNext.Domain.Common;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.UnitTests.Support;

namespace TallyNext.UnitTests.Application;

public sealed class VoucherWorkflowTests
{
    private static readonly DateOnly May10 = new(2026, 5, 10);

    [Fact]
    public async Task Balanced_voucher_is_saved_locally_numbered_in_device_series_and_queued_for_upload()
    {
        await using var host = await LocalTestHost.CreateAsync(seriesCode: "D7K");
        var books = await TestBooks.CreateAsync(host);

        var voucher = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Payment, May10, (books.Rent, DrCr.Debit, 12_500m), (books.Bank, DrCr.Credit, 12_500m))));

        Assert.Equal(VoucherStatus.Posted, voucher.Status);
        Assert.Equal("D7K/1", voucher.Number);
        Assert.Equal(12_500m, voucher.Total);
        Assert.Equal("2026-27", voucher.FinancialYear);

        var stored = await host.QueryAsync(db => db.Vouchers.Include(v => v.Lines).SingleAsync(v => v.Id == voucher.Id));
        Assert.Equal(1_250_000, stored.TotalMinor);
        Assert.True(stored.IsBalanced);
        Assert.Equal(0, stored.Version); // never accepted by the server yet

        var outbox = await host.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == voucher.Id));
        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        Assert.Equal(SyncEntityTypes.Voucher, outbox.EntityType);
        Assert.Equal(0, outbox.BaseVersion);
        var payload = SyncJson.Deserialize<VoucherSnapshot>(outbox.PayloadJson);
        Assert.Equal(VoucherStatus.Posted, payload.Status);
        Assert.Equal(2, payload.Lines.Count);
    }

    [Fact]
    public async Task Unbalanced_voucher_is_rejected_and_nothing_is_saved_or_numbered()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var before = await host.QueryAsync(db => db.OutboxEntries.CountAsync());

        var ex = await Assert.ThrowsAsync<DomainException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 100m), (books.Capital, DrCr.Credit, 90m)))));
        Assert.Equal("voucher.unbalanced", ex.Code);

        Assert.Equal(0, await host.QueryAsync(db => db.Vouchers.CountAsync()));
        Assert.Equal(before, await host.QueryAsync(db => db.OutboxEntries.CountAsync()));

        // The next good voucher still gets number 1: the failed attempt consumed nothing.
        var ok = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 100m), (books.Capital, DrCr.Credit, 100m))));
        Assert.Equal("D1/1", ok.Number);
    }

    [Fact]
    public async Task Amounts_with_more_than_two_decimals_are_rejected_not_rounded()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 10.005m), (books.Capital, DrCr.Credit, 10.005m)))));
        Assert.Equal("money.precision", ex.Code);
    }

    [Fact]
    public async Task Numbers_are_sequential_per_type_and_financial_year()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        async Task<string?> Post(VoucherBaseType type, DateOnly date) =>
            (await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
                type == VoucherBaseType.Journal
                    ? books.Voucher(type, date, (books.Rent, DrCr.Debit, 1m), (books.Capital, DrCr.Credit, 1m))
                    : books.Voucher(type, date, (books.Rent, DrCr.Debit, 1m), (books.Cash, DrCr.Credit, 1m))))).Number;

        Assert.Equal("D1/1", await Post(VoucherBaseType.Journal, May10));
        Assert.Equal("D1/2", await Post(VoucherBaseType.Journal, May10));
        Assert.Equal("D1/1", await Post(VoucherBaseType.Payment, May10));
        Assert.Equal("D1/1", await Post(VoucherBaseType.Journal, new DateOnly(2027, 4, 2))); // new financial year
        Assert.Equal("D1/3", await Post(VoucherBaseType.Journal, new DateOnly(2027, 3, 31)));
    }

    [Fact]
    public async Task Retrying_a_create_with_the_same_id_or_key_does_not_duplicate()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var input = books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 5m), (books.Capital, DrCr.Credit, 5m));
        input.Id = Ids.New();
        input.IdempotencyKey = "retry-key-1";

        var first = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));
        var second = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));
        input.Id = null;
        var third = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, third.Id);
        Assert.Equal(1, await host.QueryAsync(db => db.Vouchers.CountAsync()));
        Assert.Equal(2, await host.QueryAsync(db => db.VoucherLines.CountAsync()));
    }

    [Fact]
    public async Task Draft_can_be_edited_then_posted_and_only_posted_vouchers_affect_balances()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var input = books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 10m), (books.Capital, DrCr.Credit, 7m));
        input.Post = false;
        var draft = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));
        Assert.Equal(VoucherStatus.Draft, draft.Status);
        Assert.Null(draft.Number);

        var tbDraft = await host.RunAsync<TallyNext.Application.Reports.ReportService, TallyNext.Contracts.Reports.TrialBalanceReport>(
            s => s.TrialBalanceAsync(books.CompanyId, TestBooks.BooksBeginning, May10));
        Assert.Equal(0m, tbDraft.TotalPeriodDebit);

        input.Lines[1].Amount = 10m;
        input.Post = true;
        var posted = await host.RunAsync<VoucherService, VoucherDto>(s => s.UpdateDraftAsync(books.CompanyId, draft.Id, input));
        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.Equal("D1/1", posted.Number);

        var tb = await host.RunAsync<TallyNext.Application.Reports.ReportService, TallyNext.Contracts.Reports.TrialBalanceReport>(
            s => s.TrialBalanceAsync(books.CompanyId, TestBooks.BooksBeginning, May10));
        Assert.Equal(10m, tb.TotalPeriodDebit);
        Assert.Equal(10m, tb.TotalPeriodCredit);

        // Two local saves of the same never-sent voucher collapse into one outbox entry with the latest state.
        var entries = await host.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == draft.Id).ToListAsync());
        Assert.Single(entries);
        Assert.Equal(VoucherStatus.Posted, SyncJson.Deserialize<VoucherSnapshot>(entries[0].PayloadJson).Status);
    }

    [Fact]
    public async Task Posted_voucher_is_corrected_by_reversal_only_once()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var original = await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
            books.Voucher(VoucherBaseType.Receipt, May10, (books.Cash, DrCr.Debit, 900m), (books.Customer, DrCr.Credit, 900m))));

        var update = books.Voucher(VoucherBaseType.Receipt, May10, (books.Cash, DrCr.Debit, 1m), (books.Customer, DrCr.Credit, 1m));
        var immutable = await Assert.ThrowsAsync<DomainException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.UpdateDraftAsync(books.CompanyId, original.Id, update)));
        Assert.Equal("voucher.posted_immutable", immutable.Code);

        var reversal = await host.RunAsync<VoucherService, VoucherDto>(s => s.ReverseAsync(books.CompanyId, original.Id, new ReverseVoucherRequest { Date = May10.AddDays(1) }));
        Assert.Equal(original.Id, reversal.ReversalOfVoucherId);
        Assert.Equal("D1/2", reversal.Number);

        var again = await Assert.ThrowsAsync<ConflictException>(() => host.RunAsync<VoucherService, VoucherDto>(s => s.ReverseAsync(books.CompanyId, original.Id, new ReverseVoucherRequest { Date = May10.AddDays(2) })));
        Assert.Equal("voucher.already_reversed", again.Code);

        var reloaded = await host.RunAsync<VoucherService, VoucherDto>(s => s.GetAsync(books.CompanyId, original.Id));
        Assert.Equal(reversal.Id, reloaded.ReversedByVoucherId);
        Assert.Equal(VoucherStatus.Posted, reloaded.Status);

        var cashReport = await host.RunAsync<TallyNext.Application.Reports.ReportService, TallyNext.Contracts.Reports.LedgerReport>(
            s => s.LedgerAsync(books.CompanyId, books.Cash, TestBooks.BooksBeginning, May10.AddDays(5)));
        Assert.Equal(0m, cashReport.ClosingDebit);
        Assert.Equal(0m, cashReport.ClosingCredit);
        Assert.Equal(2, cashReport.Rows.Items.Count);
    }

    [Fact]
    public async Task Ledger_used_in_a_voucher_cannot_be_deleted()
    {
        await using var host = await LocalTestHost.CreateAsync();
        var books = await TestBooks.CreateAsync(host);
        var input = books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 1m), (books.Capital, DrCr.Credit, 1m));
        input.Post = false;
        await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId, input));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            host.RunAsync<TallyNext.Application.Accounts.ChartOfAccountsService>(s => s.DeleteLedgerAsync(books.CompanyId, books.Rent)));
        Assert.Equal("ledger.in_use", ex.Code);
    }

    [Fact]
    public async Task Saved_data_and_pending_uploads_survive_an_application_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tallynext-restart-{Guid.NewGuid():N}.db");
        try
        {
            Guid userId;
            Guid companyId;
            Guid voucherId;
            await using (var host = await LocalTestHost.CreateAsync(file))
            {
                userId = host.UserId;
                var books = await TestBooks.CreateAsync(host);
                companyId = books.CompanyId;
                voucherId = (await host.RunAsync<VoucherService, VoucherDto>(s => s.CreateAsync(books.CompanyId,
                    books.Voucher(VoucherBaseType.Journal, May10, (books.Rent, DrCr.Debit, 42m), (books.Capital, DrCr.Credit, 42m))))).Id;

                // Simulate a crash during upload: the entry is left in flight.
                await host.QueryAsync(db => db.OutboxEntries.Where(e => e.EntityId == voucherId)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, OutboxStatus.InFlight)));
            }

            await using (var restarted = await LocalTestHost.CreateAsync(file, userId: userId))
            {
                var voucher = await restarted.RunAsync<VoucherService, VoucherDto>(s => s.GetAsync(companyId, voucherId));
                Assert.Equal(42m, voucher.Total);
                var entry = await restarted.QueryAsync(db => db.OutboxEntries.SingleAsync(e => e.EntityId == voucherId));
                Assert.Equal(OutboxStatus.Pending, entry.Status);
                Assert.True(await restarted.QueryAsync(db => db.OutboxEntries.CountAsync(e => e.Status == OutboxStatus.Pending)) > 10);
            }
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(file + suffix);
            }
        }
    }
}
