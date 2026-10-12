using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Contracts;
using TallyNext.Contracts.Audit;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Companies;

namespace TallyNext.Application.Audit;

/// <summary>Read-only access to the audit trail and verification of its hash chain.</summary>
public sealed class AuditService(IAccountingDbContext db, IAccessGuard guard)
{
    private const int VerifyBatchSize = 1000;

    public async Task<PagedResult<AuditEntryDto>> ListAsync(Guid companyId, AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await guard.RequireAsync(companyId, Permission.AuditView, cancellationToken);
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);

        var entries = db.AuditEntries.AsNoTracking().Where(a => a.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(a => a.EntityType == query.EntityType);
        }

        if (query.EntityId is { } entityId)
        {
            entries = entries.Where(a => a.EntityId == entityId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(a => a.Action.StartsWith(query.Action));
        }

        if (query.FromUtc is { } fromUtc)
        {
            entries = entries.Where(a => a.OccurredAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            entries = entries.Where(a => a.OccurredAtUtc <= toUtc);
        }

        var total = await entries.LongCountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(a => a.ChainSequence)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditEntryDto(
                a.Id, a.ChainSequence, a.OccurredAtUtc, a.UserId, a.DeviceId, a.Source, a.Action, a.EntityType,
                a.EntityId, a.Summary, a.ChangesJson, a.CorrelationId, a.ChangeId))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditEntryDto>(items, page, pageSize, total);
    }

    /// <summary>
    /// Recomputes every hash of the company's chain in sequence order. Detects edited, deleted, inserted or
    /// re-ordered records. Streams in batches so memory use is bounded.
    /// </summary>
    public async Task<AuditVerificationResult> VerifyAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.AuditView, cancellationToken);
        return await VerifyChainAsync(db, companyId, cancellationToken);
    }

    /// <summary>Verification without an authorization check, for maintenance jobs and tests.</summary>
    internal static async Task<AuditVerificationResult> VerifyChainAsync(IAccountingDbContext db, Guid? companyId, CancellationToken cancellationToken)
    {
        var expectedPrevious = AuditHasher.GenesisHash;
        long expectedSequence = 1;
        long checkedCount = 0;
        while (true)
        {
            var from = expectedSequence;
            var batch = await db.AuditEntries.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.ChainSequence >= from)
                .OrderBy(a => a.ChainSequence)
                .Take(VerifyBatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return new AuditVerificationResult(true, checkedCount, null, null);
            }

            foreach (var entry in batch)
            {
                if (entry.ChainSequence != expectedSequence)
                {
                    return Broken(checkedCount, expectedSequence, $"Expected sequence {expectedSequence} but found {entry.ChainSequence} (missing or inserted record).");
                }

                if (!string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                {
                    return Broken(checkedCount, entry.ChainSequence, "The previous-hash link does not match the preceding record.");
                }

                var recomputed = AuditHasher.Compute(entry, entry.ChainSequence, entry.PreviousHash);
                if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
                {
                    return Broken(checkedCount, entry.ChainSequence, "The record content does not match its hash (modified record).");
                }

                expectedPrevious = entry.Hash;
                expectedSequence++;
                checkedCount++;
            }
        }
    }

    private static AuditVerificationResult Broken(long checkedCount, long sequence, string problem) =>
        new(false, checkedCount, sequence, problem);
}
