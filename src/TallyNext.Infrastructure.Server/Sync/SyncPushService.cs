using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TallyNext.Application.Common;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Common;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.Infrastructure.Server.Services;

namespace TallyNext.Infrastructure.Server.Sync;

/// <summary>
/// Processes a device upload. Each change runs in its own DI scope and transaction, in the device's order:
/// one bad change never blocks unrelated ones, and every accepted change is committed together with its
/// idempotency record, so a retry after any crash returns the original result instead of re-applying.
/// </summary>
public sealed class SyncPushService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SyncPushService> logger)
{
    private sealed record Failure(SyncOutcome Outcome, string Code, string Message, long? ServerVersion = null, System.Text.Json.JsonElement? ServerSnapshot = null);

    public async Task<SyncPushResponse> PushAsync(ClaimsPrincipal principal, SyncPushRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != SyncProtocol.ProtocolVersion)
        {
            throw new SyncVersionUnsupportedException($"Protocol version {request.ProtocolVersion} is not supported; the server speaks {SyncProtocol.ProtocolVersion}.");
        }

        if (request.Changes.Any(c => c.SchemaVersion != SyncProtocol.SchemaVersion))
        {
            throw new SyncVersionUnsupportedException($"Only schema version {SyncProtocol.SchemaVersion} is supported. Update the application.");
        }

        if (request.Changes.Count > SyncProtocol.MaxPushBatchSize)
        {
            throw new ValidationFailedException("sync.batch_too_large", $"At most {SyncProtocol.MaxPushBatchSize} changes can be uploaded at once.");
        }

        var userId = TallyClaims.UserIdOf(principal) ?? throw new UnauthenticatedException();
        if (TallyClaims.DeviceIdOf(principal) != request.DeviceId)
        {
            throw new ForbiddenException("The access token was not issued for this device.");
        }

        var device = await LoadActiveDeviceAsync(request.DeviceId, cancellationToken);
        var results = new List<SyncChangeResult>(request.Changes.Count);
        var blocked = new HashSet<Guid>();
        foreach (var change in request.Changes.OrderBy(c => c.LocalSequence))
        {
            if (blocked.Contains(change.EntityId))
            {
                results.Add(new SyncChangeResult(change.ChangeId, SyncOutcome.Deferred, ErrorCode: "sync.blocked_by_previous",
                    Message: "An earlier change to the same record was not accepted."));
                continue;
            }

            var result = await ProcessAsync(principal, userId, device, change, cancellationToken);
            if (result.Outcome is not (SyncOutcome.Accepted or SyncOutcome.Duplicate))
            {
                blocked.Add(change.EntityId);
            }

            results.Add(result);
        }

        await TouchDeviceAsync(device.Id, cancellationToken);
        return new SyncPushResponse(results, clock.GetUtcNow().UtcDateTime);
    }

    private async Task<SyncChangeResult> ProcessAsync(ClaimsPrincipal principal, Guid userId, Device device, SyncChangeUpload change, CancellationToken cancellationToken)
    {
        var hash = HashOf(change);
        Failure failure;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = Prepare(scope.ServiceProvider, principal, device, change);
            try
            {
                await using var write = await db.BeginCompanyWriteAsync(change.CompanyId, cancellationToken);

                // Re-checked inside the lock: a concurrent retry of the same change waits here, then sees the result.
                var replay = await ReplayAsync(db, device.Id, change.ChangeId, hash, cancellationToken);
                if (replay is not null)
                {
                    return replay;
                }

                var entity = await scope.ServiceProvider.GetRequiredService<SyncChangeApplier>().ApplyAsync(userId, device, change, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                var result = new SyncChangeResult(change.ChangeId, SyncOutcome.Accepted, entity.Version);
                db.ProcessedChanges.Add(NewProcessed(device.Id, change, hash, result));
                if (change.ResolvesConflictId is { } conflictId)
                {
                    await MarkConflictResolvedAsync(db, change.CompanyId, conflictId, userId, cancellationToken);
                }

                await db.SaveChangesAsync(cancellationToken);
                await write.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (Classify(ex) is { } classified)
            {
                failure = classified;
                if (failure.Code == "sync.server_error")
                {
                    logger.LogError(ex, "Unexpected error applying change {ChangeId} ({EntityType} {EntityId})", change.ChangeId, change.EntityType, change.EntityId);
                }
                else
                {
                    logger.LogInformation("Change {ChangeId} ({EntityType}) not applied: {Outcome} {Code}", change.ChangeId, change.EntityType, failure.Outcome, failure.Code);
                }
            }
        }

        if (failure.Outcome == SyncOutcome.Deferred)
        {
            // Not recorded: a deferred change must really be retried later.
            return new SyncChangeResult(change.ChangeId, SyncOutcome.Deferred, ErrorCode: failure.Code, Message: failure.Message);
        }

        return await RecordFailureAsync(principal, userId, device, change, hash, failure, cancellationToken);
    }

    private async Task<SyncChangeResult> RecordFailureAsync(
        ClaimsPrincipal principal, Guid userId, Device device, SyncChangeUpload change, string hash, Failure failure, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Prepare(scope.ServiceProvider, principal, device, change);
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
        var companyKnownToUser = failure.Outcome != SyncOutcome.Forbidden;

        await using var write = companyKnownToUser
            ? await db.BeginCompanyWriteAsync(change.CompanyId, cancellationToken)
            : null;
        await using var plain = companyKnownToUser ? null : await db.Database.BeginTransactionAsync(cancellationToken);

        var replay = await ReplayAsync(db, device.Id, change.ChangeId, hash, cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        Guid? conflictId = null;
        if (failure.Outcome == SyncOutcome.Conflict)
        {
            var conflict = new SyncConflictRecord
            {
                Id = Ids.New(),
                CompanyId = change.CompanyId,
                EntityType = change.EntityType,
                EntityId = change.EntityId,
                DeviceId = device.Id,
                ChangeId = change.ChangeId,
                UserId = userId,
                BaseVersion = change.BaseVersion,
                ServerVersion = failure.ServerVersion ?? 0,
                Reason = failure.Code,
                ClientPayloadJson = change.Payload.GetRawText(),
                ServerPayloadJson = failure.ServerSnapshot?.GetRawText(),
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.SyncConflicts.Add(conflict);
            conflictId = conflict.Id;
        }

        var result = new SyncChangeResult(
            change.ChangeId, failure.Outcome, ErrorCode: failure.Code, Message: failure.Message, ConflictId: conflictId,
            ServerVersion: failure.ServerVersion, ServerPayload: failure.ServerSnapshot);
        db.ProcessedChanges.Add(NewProcessed(device.Id, change, hash, result));
        audit.Record(
            companyKnownToUser ? change.CompanyId : null,
            failure.Outcome switch
            {
                SyncOutcome.Conflict => "sync.conflict",
                SyncOutcome.Forbidden => "sync.forbidden",
                _ => "sync.rejected",
            },
            change.EntityType,
            change.EntityId,
            $"{failure.Code}: {failure.Message}",
            changeId: change.ChangeId);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (write is not null)
            {
                await write.CommitAsync(cancellationToken);
            }
            else if (plain is not null)
            {
                await plain.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException)
        {
            // A concurrent request recorded this change first; return what it recorded.
            await using var retryScope = scopes.CreateAsyncScope();
            var retryDb = retryScope.ServiceProvider.GetRequiredService<ServerDbContext>();
            return await ReplayAsync(retryDb, device.Id, change.ChangeId, hash, cancellationToken) ?? throw new InvalidOperationException("Processed change vanished.");
        }

        return result;
    }

    private static ServerDbContext Prepare(IServiceProvider services, ClaimsPrincipal principal, Device device, SyncChangeUpload change)
    {
        var context = services.GetRequiredService<ServerExecutionContext>();
        context.Use(principal);
        context.UseSyncSource();
        var origin = services.GetRequiredService<SyncOriginContext>();
        origin.DeviceId = device.Id;
        origin.ChangeId = change.ChangeId;
        return services.GetRequiredService<ServerDbContext>();
    }

    private static async Task<SyncChangeResult?> ReplayAsync(ServerDbContext db, Guid deviceId, Guid changeId, string hash, CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedChanges.AsNoTracking()
            .SingleOrDefaultAsync(p => p.DeviceId == deviceId && p.ChangeId == changeId, cancellationToken);
        if (processed is null)
        {
            return null;
        }

        if (!string.Equals(processed.PayloadHash, hash, StringComparison.Ordinal))
        {
            return new SyncChangeResult(changeId, SyncOutcome.Rejected, ErrorCode: "sync.change_id_reused",
                Message: "This change id was already used for different content.");
        }

        var stored = SyncJson.Deserialize<SyncChangeResult>(processed.ResultJson);
        return stored.Outcome == SyncOutcome.Accepted ? stored with { Outcome = SyncOutcome.Duplicate } : stored;
    }

    private ProcessedChange NewProcessed(Guid deviceId, SyncChangeUpload change, string hash, SyncChangeResult result) => new()
    {
        DeviceId = deviceId,
        ChangeId = change.ChangeId,
        CompanyId = change.CompanyId,
        PayloadHash = hash,
        Outcome = result.Outcome,
        ResultJson = SyncJson.Serialize(result),
        ProcessedAtUtc = clock.GetUtcNow().UtcDateTime,
    };

    private async Task MarkConflictResolvedAsync(ServerDbContext db, Guid companyId, Guid conflictId, Guid userId, CancellationToken cancellationToken)
    {
        var conflict = await db.SyncConflicts.SingleOrDefaultAsync(c => c.Id == conflictId && c.CompanyId == companyId, cancellationToken);
        if (conflict is { Status: Contracts.Sync.SyncConflictStatus.Open })
        {
            conflict.Status = Contracts.Sync.SyncConflictStatus.ResolvedKeepClient;
            conflict.ResolvedByUserId = userId;
            conflict.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
            conflict.ResolutionNote = "Device user kept their version.";
        }
    }

    private async Task<Device> LoadActiveDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null || device.IsRevoked)
        {
            throw new ForbiddenException("This device is not registered or has been revoked.");
        }

        return device;
    }

    private async Task TouchDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Devices.Where(d => d.Id == deviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAtUtc, now), cancellationToken);
    }

    private static string HashOf(SyncChangeUpload change)
    {
        var canonical = $"{change.CompanyId:D}|{change.EntityType}|{change.EntityId:D}|{(int)change.Operation}|{change.BaseVersion}|{change.ResolvesConflictId:D}|{change.Payload.GetRawText()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static Failure? Classify(Exception ex) => ex switch
    {
        OperationCanceledException => null,
        SyncConflictException c => new Failure(SyncOutcome.Conflict, c.Reason, "The record was changed on the server since this device last synchronized.", c.ServerVersion, c.ServerSnapshot),
        SyncDeferredException d => new Failure(SyncOutcome.Deferred, d.Code, d.Message),
        SyncRejectedException r => new Failure(SyncOutcome.Rejected, r.Code, r.Message),
        DomainException d => new Failure(SyncOutcome.Rejected, d.Code, d.Message),
        ValidationFailedException v => new Failure(SyncOutcome.Rejected, v.Code, v.Message),
        ConflictException c => new Failure(SyncOutcome.Rejected, c.Code, c.Message),
        ForbiddenException f => new Failure(SyncOutcome.Forbidden, f.Code, f.Message),
        UnauthenticatedException u => new Failure(SyncOutcome.Forbidden, u.Code, u.Message),
        NotFoundException n when n.Code == "company.not_found" => new Failure(SyncOutcome.Forbidden, "company.access_denied", "You do not have access to this company."),
        NotFoundException n => new Failure(SyncOutcome.Rejected, n.Code, n.Message),
        DbUpdateConcurrencyException => new Failure(SyncOutcome.Conflict, "sync.version_mismatch", "The record was changed concurrently."),
        DbUpdateException => new Failure(SyncOutcome.Rejected, "sync.constraint_violation", "The change violates a database constraint (for example a duplicate)."),
        _ => new Failure(SyncOutcome.Deferred, "sync.server_error", "The server could not process this change. It will be retried."),
    };
}
