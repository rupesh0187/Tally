using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Accounts;
using TallyNext.Domain.Common;
using TallyNext.Domain.Companies;
using TallyNext.Domain.Vouchers;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.Infrastructure.Persistence;

namespace TallyNext.Infrastructure.Local.Sync;

public sealed record SyncCycleReport(int Uploaded, int Conflicts, int Rejected, int Deferred, int Downloaded, int SkippedLocalEdits, IReadOnlyList<Guid> RevokedCompanies);

/// <summary>
/// One synchronization cycle: upload the outbox in order, then download every accessible company's change log.
/// Safe to interrupt at any point: state transitions are transactional and the server deduplicates retries.
/// Only one cycle runs at a time.
/// </summary>
public sealed class SyncEngine(
    IServiceScopeFactory scopes,
    ISyncTransport transport,
    DeviceSession session,
    SyncStatusTracker status,
    TimeProvider clock,
    ILogger<SyncEngine> logger)
{
    private static readonly OutboxStatus[] UnresolvedStatuses = [OutboxStatus.Conflict, OutboxStatus.Rejected, OutboxStatus.Forbidden];
    private static readonly OutboxStatus[] LocallyOpenStatuses = [OutboxStatus.Pending, OutboxStatus.InFlight, OutboxStatus.Conflict, OutboxStatus.Rejected, OutboxStatus.Forbidden];
    private readonly SemaphoreSlim _cycle = new(1, 1);

    public async Task<SyncCycleReport> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        await _cycle.WaitAsync(cancellationToken);
        try
        {
            var user = session.User ?? throw new InvalidOperationException("Sign in to synchronize.");
            status.Publish(s => s with { State = SyncState.Syncing, Message = null });

            var counters = new Counters();
            await PushAsync(user.UserId, counters, cancellationToken);
            var revoked = await PullAsync(user.UserId, counters, cancellationToken);
            await RecordSuccessAsync(cancellationToken);
            await RefreshStatusAsync(cancellationToken);
            logger.LogInformation("Sync cycle complete: {Uploaded} uploaded, {Downloaded} downloaded, {Conflicts} conflicts", counters.Uploaded, counters.Downloaded, counters.Conflicts);
            return new SyncCycleReport(counters.Uploaded, counters.Conflicts, counters.Rejected, counters.Deferred, counters.Downloaded, counters.Skipped, revoked);
        }
        catch (SyncTransportException ex)
        {
            var state = ex.Failure switch
            {
                SyncTransportFailure.Offline => SyncState.Offline,
                SyncTransportFailure.Unauthorized or SyncTransportFailure.AccessRevoked => SyncState.SignInRequired,
                SyncTransportFailure.UpgradeRequired => SyncState.UpdateRequired,
                _ => SyncState.Error,
            };
            await RefreshStatusAsync(cancellationToken, state, ex.Message);
            throw;
        }
        finally
        {
            _cycle.Release();
        }
    }

    /// <summary>Recomputes pending and attention counts (call after local saves so the UI shows unsynced work).</summary>
    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default, SyncState? state = null, string? message = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var pending = await db.OutboxEntries.CountAsync(e => e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.InFlight, cancellationToken);
        var attention = await db.LocalConflicts.CountAsync(c => c.Status == LocalConflictStatus.Open, cancellationToken);
        var lastSuccess = await db.DeviceRegistrations.AsNoTracking().Select(r => r.LastSuccessfulSyncAtUtc).SingleOrDefaultAsync(cancellationToken);
        var resolved = state ?? (session.User is null
            ? SyncState.NotSignedIn
            : attention > 0 ? SyncState.NeedsAttention : pending > 0 ? SyncState.PendingChanges : SyncState.UpToDate);
        status.Publish(s => new SyncStatusSnapshot(resolved, pending, attention, lastSuccess, message, s.NextAttemptUtc));
    }

    // ------------------------------------------------------------------ upload

    private async Task PushAsync(Guid userId, Counters counters, CancellationToken cancellationToken)
    {
        while (true)
        {
            List<OutboxEntry> batch;
            Guid deviceId;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                deviceId = (await db.DeviceRegistrations.AsNoTracking().SingleAsync(cancellationToken)).DeviceId;

                // Hold back rows that have an unresolved earlier change, or an earlier pending change by another user.
                var blocked = db.OutboxEntries
                    .Where(e => UnresolvedStatuses.Contains(e.Status) || (e.Status == OutboxStatus.Pending && e.UserId != userId))
                    .Select(e => e.EntityId);
                batch = await db.OutboxEntries
                    .Where(e => e.Status == OutboxStatus.Pending && e.UserId == userId && !blocked.Contains(e.EntityId))
                    .OrderBy(e => e.LocalSequence)
                    .Take(SyncProtocol.MaxPushBatchSize)
                    .ToListAsync(cancellationToken);
                if (batch.Count == 0)
                {
                    return;
                }

                var now = clock.GetUtcNow().UtcDateTime;
                foreach (var entry in batch)
                {
                    entry.Status = OutboxStatus.InFlight;
                    entry.Attempts++;
                    entry.LastAttemptAtUtc = now;
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            SyncPushResponse response;
            try
            {
                var request = new SyncPushRequest(deviceId, SyncProtocol.ProtocolVersion, batch.Select(ToUpload).ToList());
                response = await transport.PushAsync(request, cancellationToken);
            }
            catch
            {
                // The request may or may not have reached the server; either way the entries are safe to resend.
                await ResetInFlightAsync(batch.Select(e => e.LocalSequence).ToList(), CancellationToken.None);
                throw;
            }

            var progressed = await ApplyPushResultsAsync(batch.Select(e => e.LocalSequence).ToList(), response, counters, cancellationToken);
            if (!progressed || batch.Count < SyncProtocol.MaxPushBatchSize)
            {
                return;
            }
        }
    }

    private async Task<bool> ApplyPushResultsAsync(IReadOnlyList<long> sequences, SyncPushResponse response, Counters counters, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        using var apply = db.BeginRemoteApply();

        var results = response.Results.ToDictionary(r => r.ChangeId);
        var entries = await db.OutboxEntries.Where(e => sequences.Contains(e.LocalSequence)).OrderBy(e => e.LocalSequence).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var progressed = false;
        foreach (var entry in entries)
        {
            if (!results.TryGetValue(entry.ChangeId, out var result))
            {
                entry.Status = OutboxStatus.Pending;
                continue;
            }

            entry.LastErrorCode = result.ErrorCode;
            entry.LastError = Truncate(result.Message, 1000);
            switch (result.Outcome)
            {
                case SyncOutcome.Accepted:
                case SyncOutcome.Duplicate:
                    entry.Status = OutboxStatus.Accepted;
                    entry.AcceptedVersion = result.NewVersion;
                    entry.CompletedAtUtc = now;
                    if (result.NewVersion is { } version)
                    {
                        await SetVersionAsync(db, entry.EntityType, entry.EntityId, version, cancellationToken);
                        await db.OutboxEntries
                            .Where(e => e.EntityId == entry.EntityId && e.LocalSequence != entry.LocalSequence && e.BaseVersion == entry.BaseVersion
                                        && (e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.InFlight))
                            .ExecuteUpdateAsync(s => s.SetProperty(e => e.BaseVersion, version), cancellationToken);
                    }

                    counters.Uploaded++;
                    progressed = true;
                    break;
                case SyncOutcome.Conflict:
                case SyncOutcome.Rejected:
                case SyncOutcome.Forbidden:
                    entry.Status = result.Outcome switch
                    {
                        SyncOutcome.Conflict => OutboxStatus.Conflict,
                        SyncOutcome.Rejected => OutboxStatus.Rejected,
                        _ => OutboxStatus.Forbidden,
                    };
                    entry.ConflictId = result.ConflictId;
                    db.LocalConflicts.Add(new LocalConflict
                    {
                        Id = Ids.New(),
                        OutboxSequence = entry.LocalSequence,
                        ServerConflictId = result.ConflictId,
                        CompanyId = entry.CompanyId,
                        EntityType = entry.EntityType,
                        EntityId = entry.EntityId,
                        Kind = result.Outcome.ToString(),
                        ErrorCode = result.ErrorCode,
                        Message = Truncate(result.Message, 1000),
                        LocalPayloadJson = entry.PayloadJson,
                        ServerPayloadJson = result.ServerPayload?.GetRawText(),
                        ServerVersion = result.ServerVersion,
                        DetectedAtUtc = now,
                    });
                    if (result.Outcome == SyncOutcome.Conflict)
                    {
                        counters.Conflicts++;
                    }
                    else
                    {
                        counters.Rejected++;
                    }

                    progressed = true;
                    break;
                default:
                    entry.Status = OutboxStatus.Pending;
                    counters.Deferred++;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return progressed;
    }

    private async Task ResetInFlightAsync(IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        await db.OutboxEntries
            .Where(e => sequences.Contains(e.LocalSequence) && e.Status == OutboxStatus.InFlight)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, OutboxStatus.Pending), cancellationToken);
    }

    private static SyncChangeUpload ToUpload(OutboxEntry e)
    {
        using var document = JsonDocument.Parse(e.PayloadJson);
        return new SyncChangeUpload(
            e.ChangeId, e.LocalSequence, e.CompanyId, e.EntityType, e.EntityId, e.Operation, e.BaseVersion, e.SchemaVersion,
            e.ClientModifiedAtUtc, document.RootElement.Clone(), e.ResolvesConflictId);
    }

    // ------------------------------------------------------------------ download

    private async Task<IReadOnlyList<Guid>> PullAsync(Guid userId, Counters counters, CancellationToken cancellationToken)
    {
        var companies = await transport.GetCompaniesAsync(cancellationToken);
        var revoked = await MarkRevokedCompaniesAsync(userId, companies.Select(c => c.CompanyId).ToHashSet(), cancellationToken);
        foreach (var company in companies)
        {
            while (true)
            {
                long cursor;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                    cursor = await db.SyncCursors.AsNoTracking().Where(c => c.CompanyId == company.CompanyId).Select(c => c.Cursor).SingleOrDefaultAsync(cancellationToken);
                }

                SyncPullResponse page;
                try
                {
                    page = await transport.PullAsync(company.CompanyId, cursor, SyncProtocol.DefaultPullBatchSize, cancellationToken);
                }
                catch (SyncTransportException ex) when (ex.Failure == SyncTransportFailure.NoAccess)
                {
                    await MarkCompanyRevokedAsync(userId, company.CompanyId, cancellationToken);
                    revoked.Add(company.CompanyId);
                    break;
                }

                if (page.SchemaVersion > SyncProtocol.SchemaVersion)
                {
                    throw new SyncTransportException(SyncTransportFailure.UpgradeRequired, "The server sends data this version of the application cannot read. Update the application.");
                }

                await ApplyPageAsync(page, counters, cancellationToken);
                if (!page.HasMore)
                {
                    break;
                }
            }
        }

        return revoked;
    }

    private async Task ApplyPageAsync(SyncPullResponse page, Counters counters, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var seriesCode = await db.DeviceRegistrations.AsNoTracking().Select(r => r.SeriesCode).SingleOrDefaultAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", cancellationToken);
        using var apply = db.BeginRemoteApply();

        var entityIds = page.Changes.Select(c => c.EntityId).Distinct().ToList();
        var locallyOpen = (await db.OutboxEntries
                .Where(e => entityIds.Contains(e.EntityId) && LocallyOpenStatuses.Contains(e.Status))
                .Select(e => e.EntityId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var change in page.Changes)
        {
            if (locallyOpen.Contains(change.EntityId))
            {
                // Unsent local edits are never overwritten; the upload will surface any conflict for a person to decide.
                counters.Skipped++;
                continue;
            }

            var snapshot = change.Payload.Deserialize(SyncJson.SnapshotTypeOf(change.EntityType), SyncJson.Options)
                ?? throw new JsonException($"Empty payload for {change.EntityType} {change.EntityId}.");
            var entity = await SnapshotApplier.UpsertAsync(db, snapshot, change.Version, cancellationToken);
            if (entity is Voucher { SeriesCode: { } code, SequenceNumber: { } sequence } voucher && code == seriesCode)
            {
                await AdvanceLocalSeriesAsync(db, voucher, sequence, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            counters.Downloaded++;
        }

        var cursor = await db.SyncCursors.SingleOrDefaultAsync(c => c.CompanyId == page.CompanyId, cancellationToken);
        if (cursor is null)
        {
            cursor = new SyncCursor { CompanyId = page.CompanyId };
            db.SyncCursors.Add(cursor);
        }

        cursor.Cursor = Math.Max(cursor.Cursor, page.NextCursor);
        cursor.LastPulledAtUtc = clock.GetUtcNow().UtcDateTime;
        cursor.AccessRevoked = false;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AdvanceLocalSeriesAsync(LocalDbContext db, Voucher voucher, long sequence, CancellationToken cancellationToken)
    {
        var series = await db.VoucherNumberSeries.SingleOrDefaultAsync(
            s => s.CompanyId == voucher.CompanyId && s.VoucherTypeId == voucher.VoucherTypeId && s.FinancialYearStart == voucher.FinancialYearStart && s.SeriesCode == voucher.SeriesCode,
            cancellationToken);
        if (series is null)
        {
            series = Domain.Vouchers.VoucherNumberSeries.Create(voucher.CompanyId, voucher.VoucherTypeId, new FinancialYear(voucher.FinancialYearStart), voucher.SeriesCode!);
            db.VoucherNumberSeries.Add(series);
        }

        series.EnsureAtLeast(sequence);
    }

    /// <summary>
    /// Companies this user used to sync (known to the server: Version &gt; 0) that the server no longer lists have had
    /// access revoked. Their data stays on disk (including unsent changes) but the local membership is deactivated.
    /// </summary>
    private async Task<List<Guid>> MarkRevokedCompaniesAsync(Guid userId, IReadOnlySet<Guid> accessible, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var local = await (
                from m in db.CompanyMemberships.AsNoTracking()
                join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
                where m.UserId == userId && m.IsActive && c.Version > 0
                select c.Id)
            .ToListAsync(cancellationToken);
        var revoked = local.Where(id => !accessible.Contains(id)).ToList();
        foreach (var companyId in revoked)
        {
            await MarkCompanyRevokedAsync(userId, companyId, cancellationToken);
        }

        return revoked;
    }

    private async Task MarkCompanyRevokedAsync(Guid userId, Guid companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        await db.CompanyMemberships
            .Where(m => m.CompanyId == companyId && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false), cancellationToken);
        var cursor = await db.SyncCursors.SingleOrDefaultAsync(c => c.CompanyId == companyId, cancellationToken);
        if (cursor is null)
        {
            db.SyncCursors.Add(new SyncCursor { CompanyId = companyId, AccessRevoked = true });
        }
        else
        {
            cursor.AccessRevoked = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Access to company {CompanyId} has been revoked for user {UserId}", companyId, userId);
    }

    private async Task RecordSuccessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        await db.DeviceRegistrations.ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSuccessfulSyncAtUtc, now), cancellationToken);
    }

    private static Task SetVersionAsync(LocalDbContext db, string entityType, Guid id, long version, CancellationToken ct) => entityType switch
    {
        SyncEntityTypes.Company => db.Companies.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version), ct),
        SyncEntityTypes.AccountGroup => db.AccountGroups.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version), ct),
        SyncEntityTypes.Ledger => db.Ledgers.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version), ct),
        SyncEntityTypes.VoucherType => db.VoucherTypes.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version), ct),
        SyncEntityTypes.Voucher => db.Vouchers.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, version), ct),
        _ => Task.FromResult(0),
    };

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private sealed class Counters
    {
        public int Uploaded { get; set; }

        public int Conflicts { get; set; }

        public int Rejected { get; set; }

        public int Deferred { get; set; }

        public int Downloaded { get; set; }

        public int Skipped { get; set; }
    }
}
