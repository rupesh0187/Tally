using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Contracts.Auth;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Security;
using TallyNext.Infrastructure.Local.Session;

namespace TallyNext.Infrastructure.Local.Sync;

/// <summary>
/// Holds the session's access token in memory and the refresh token encrypted at rest (DPAPI). Refreshes the
/// access token on demand; one refresh at a time.
/// </summary>
public sealed class DeviceTokenStore(IServiceScopeFactory scopes, DeviceSession session, ISecretProtector protector, TimeProvider clock)
{
    private const string Purpose = "refresh-token";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpiresUtc;
    private Guid? _tokenUserId;

    public static string SecretKey(Guid userId) => $"refresh:{userId:N}";

    /// <summary>Stores tokens from a login or refresh and extends the user's offline sign-in window.</summary>
    public async Task StoreAsync(Guid userId, TokenResponse tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var key = SecretKey(userId);
        var secret = await db.ProtectedSecrets.SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (secret is null)
        {
            secret = new ProtectedSecret { Key = key };
            db.ProtectedSecrets.Add(secret);
        }

        secret.ProtectedValue = protector.Protect(Encoding.UTF8.GetBytes(tokens.RefreshToken), Purpose);
        secret.UpdatedAtUtc = now;

        var user = await db.LocalUsers.SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken);
        if (user is not null)
        {
            user.LastOnlineAuthAtUtc = now;
            user.OfflineGraceDays = tokens.OfflineGraceDays;
            user.OfflineAllowedUntilUtc = now.AddDays(tokens.OfflineGraceDays);
            user.IsRevoked = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        _accessToken = tokens.AccessToken;
        _accessExpiresUtc = tokens.AccessTokenExpiresAtUtc;
        _tokenUserId = userId;
    }

    public async Task<string> GetAccessTokenAsync(ISyncTransport transport, bool forceRefresh, CancellationToken cancellationToken)
    {
        var user = session.User ?? throw new SyncTransportException(SyncTransportFailure.Unauthorized, "No user is signed in.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _tokenUserId == user.UserId && _accessToken is not null && _accessExpiresUtc > clock.GetUtcNow().UtcDateTime.AddSeconds(60))
            {
                return _accessToken;
            }

            var refreshToken = await LoadRefreshTokenAsync(user.UserId, cancellationToken)
                ?? throw new SyncTransportException(SyncTransportFailure.Unauthorized, "Sign in while connected to the server to synchronize.");
            TokenResponse response;
            try
            {
                response = await transport.RefreshAsync(new RefreshRequest { RefreshToken = refreshToken, DeviceId = session.DeviceId }, cancellationToken);
            }
            catch (SyncTransportException ex) when (ex.Failure is SyncTransportFailure.Unauthorized or SyncTransportFailure.AccessRevoked)
            {
                await ForgetAsync(user.UserId, revoked: ex.Failure == SyncTransportFailure.AccessRevoked, cancellationToken);
                throw;
            }

            await StoreAsync(user.UserId, response, cancellationToken);
            return response.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops stored tokens. When the server reported revocation, offline sign-in is disabled too.</summary>
    public async Task ForgetAsync(Guid userId, bool revoked, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var key = SecretKey(userId);
        await db.ProtectedSecrets.Where(s => s.Key == key).ExecuteDeleteAsync(cancellationToken);
        if (revoked)
        {
            await db.LocalUsers.Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsRevoked, true), cancellationToken);
        }

        if (_tokenUserId == userId)
        {
            _accessToken = null;
            _tokenUserId = null;
        }
    }

    public bool HasAccessToken => _accessToken is not null;

    private async Task<string?> LoadRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var key = SecretKey(userId);
        var secret = await db.ProtectedSecrets.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
        return secret is null ? null : Encoding.UTF8.GetString(protector.Unprotect(secret.ProtectedValue, Purpose));
    }
}
