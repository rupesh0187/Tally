using System.Security.Claims;
using TallyNext.Contracts.Sync;
using TallyNext.Infrastructure.Server.Sync;

namespace TallyNext.Api.Endpoints;

internal static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").WithTags("Synchronization");

        sync.MapGet("/companies", async (SyncQueryService s, CancellationToken ct) => TypedResults.Ok(await s.CompaniesAsync(ct)))
            .WithSummary("Companies the caller may synchronize, with their latest change sequence.");

        sync.MapPost("/push", async (SyncPushRequest request, ClaimsPrincipal user, SyncPushService s, CancellationToken ct) =>
                TypedResults.Ok(await s.PushAsync(user, request, ct)))
            .WithSummary("Upload device changes. Idempotent per change id; returns a per-change outcome.");

        sync.MapGet("/pull", async (Guid companyId, long? cursor, int? limit, SyncQueryService s, CancellationToken ct) =>
                TypedResults.Ok(await s.PullAsync(companyId, cursor ?? 0, limit ?? SyncProtocol.DefaultPullBatchSize, ct)))
            .WithSummary("Download change-log entries after the cursor (cursor 0 = initial full download).");

        sync.MapGet("/entity", async (Guid companyId, string entityType, Guid entityId, SyncQueryService s, CancellationToken ct) =>
                TypedResults.Ok(await s.GetEntityAsync(companyId, entityType, entityId, ct)))
            .WithSummary("Current server state of one row (used when a device resolves a conflict in favour of the server).");

        var conflicts = app.MapGroup("/api/companies/{companyId:guid}/sync/conflicts").WithTags("Synchronization");
        conflicts.MapGet("/", async (Guid companyId, SyncConflictStatus? status, SyncQueryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ConflictsAsync(companyId, status, ct)));
        conflicts.MapPost("/{conflictId:guid}/resolve", async (Guid companyId, Guid conflictId, ResolveConflictRequest r, SyncQueryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ResolveConflictAsync(companyId, conflictId, r, ct)));
    }
}
