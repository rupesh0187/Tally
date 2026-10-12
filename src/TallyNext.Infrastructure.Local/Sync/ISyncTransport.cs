using TallyNext.Contracts.Auth;
using TallyNext.Contracts.Sync;

namespace TallyNext.Infrastructure.Local.Sync;

public enum SyncTransportFailure
{
    /// <summary>The server could not be reached (no network, DNS, refused, timeout, response lost).</summary>
    Offline,

    /// <summary>Credentials or session not accepted (HTTP 401).</summary>
    Unauthorized,

    /// <summary>The user or device is no longer allowed (HTTP 401 auth.access_revoked / 403).</summary>
    AccessRevoked,

    /// <summary>The company is unknown or not accessible to this user (HTTP 403/404).</summary>
    NoAccess,

    /// <summary>The server requires a newer application (HTTP 426).</summary>
    UpgradeRequired,

    /// <summary>The server rejected the request (HTTP 400/409) or failed (5xx).</summary>
    ServerError,
}

public sealed class SyncTransportException(SyncTransportFailure failure, string message, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public SyncTransportFailure Failure { get; } = failure;

    public string? Code { get; } = code;
}

/// <summary>The device's view of the server. Implemented over HTTPS; tests can substitute it.</summary>
public interface ISyncTransport
{
    Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncCompanyInfo>> GetCompaniesAsync(CancellationToken cancellationToken);

    Task<SyncPushResponse> PushAsync(SyncPushRequest request, CancellationToken cancellationToken);

    Task<SyncPullResponse> PullAsync(Guid companyId, long cursor, int limit, CancellationToken cancellationToken);

    Task<ServerEntityState> GetEntityAsync(Guid companyId, string entityType, Guid entityId, CancellationToken cancellationToken);
}
