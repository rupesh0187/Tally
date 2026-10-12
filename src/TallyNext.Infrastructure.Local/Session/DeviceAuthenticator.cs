using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TallyNext.Contracts.Auth;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Security;
using TallyNext.Infrastructure.Local.Sync;

namespace TallyNext.Infrastructure.Local.Session;

public enum SignInMode
{
    Online,
    Offline,
}

public sealed record SignInResult(bool Succeeded, SignInMode Mode, string? Error)
{
    public static SignInResult Fail(string error) => new(false, SignInMode.Offline, error);
}

/// <summary>
/// Desktop sign-in. Online first: the server verifies the password, registers this device, issues tokens and the
/// device stores an offline verifier. When the server is unreachable, a previously provisioned user may sign in
/// offline within the grace period; repeated failures lock offline sign-in for 15 minutes.
/// </summary>
public sealed class DeviceAuthenticator(
    IServiceScopeFactory scopes,
    DeviceSession session,
    ISyncTransport transport,
    DeviceTokenStore tokens,
    ServerEndpoint endpoint,
    ISecretProtector protector,
    TimeProvider clock,
    ILogger<DeviceAuthenticator> logger)
{
    private const string VerifierPurpose = "offline-verifier";
    private const int MaxOfflineFailures = 5;
    private static readonly TimeSpan OfflineLockout = TimeSpan.FromMinutes(15);

    public async Task<SignInResult> SignInAsync(string email, string password, string? serverUrl = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return SignInResult.Fail("Enter your e-mail and password.");
        }

        email = email.Trim();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            endpoint.Set(serverUrl);
            registration.ServerUrl = endpoint.BaseAddress!.ToString();
        }
        else if (!endpoint.IsConfigured && registration.ServerUrl is not null)
        {
            endpoint.Set(registration.ServerUrl);
        }

        if (endpoint.IsConfigured)
        {
            try
            {
                var response = await transport.LoginAsync(new LoginRequest
                {
                    Email = email,
                    Password = password,
                    DeviceId = registration.DeviceId,
                    Device = new DeviceLoginInfo { Name = registration.DeviceName, Platform = "windows" },
                }, cancellationToken);

                await CompleteOnlineSignInAsync(db, registration, response, password, cancellationToken);
                return new SignInResult(true, SignInMode.Online, null);
            }
            catch (SyncTransportException ex) when (ex.Failure == SyncTransportFailure.Offline)
            {
                logger.LogInformation("Server unreachable; trying offline sign-in");
            }
            catch (SyncTransportException ex) when (ex.Failure == SyncTransportFailure.AccessRevoked)
            {
                await db.LocalUsers.Where(u => u.NormalizedEmail == Normalize(email)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsRevoked, true), cancellationToken);
                return SignInResult.Fail(ex.Message);
            }
            catch (SyncTransportException ex)
            {
                // Wrong password, locked out, server error: never fall back to offline sign-in for an answered request.
                return SignInResult.Fail(ex.Message);
            }
        }

        return await OfflineSignInAsync(db, email, password, cancellationToken);
    }

    /// <summary>Ends the session. Stored tokens are kept so background sync can resume after the next sign-in.</summary>
    public void SignOut() => session.SignOut();

    private async Task CompleteOnlineSignInAsync(LocalDbContext db, DeviceRegistration registration, TokenResponse response, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (response.Device is { } device)
        {
            registration.SeriesCode = device.SeriesCode;
            registration.RegisteredAtUtc ??= device.RegisteredAtUtc;
            registration.IsRevoked = device.IsRevoked;
        }

        var (salt, hash) = OfflineVerifier.Create(password);
        var user = await db.LocalUsers.SingleOrDefaultAsync(u => u.UserId == response.User.Id, cancellationToken);
        if (user is null)
        {
            user = new LocalUser { UserId = response.User.Id };
            db.LocalUsers.Add(user);
        }

        user.Email = response.User.Email;
        user.NormalizedEmail = Normalize(response.User.Email);
        user.DisplayName = response.User.DisplayName;
        user.VerifierSalt = salt;
        user.ProtectedVerifier = protector.Protect(hash, VerifierPurpose);
        user.Iterations = OfflineVerifier.Iterations;
        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        user.IsRevoked = false;
        user.LastOnlineAuthAtUtc = now;
        user.OfflineGraceDays = response.OfflineGraceDays;
        user.OfflineAllowedUntilUtc = now.AddDays(response.OfflineGraceDays);
        await db.SaveChangesAsync(cancellationToken);

        await tokens.StoreAsync(response.User.Id, response, cancellationToken);
        session.SignIn(new SignedInUser(response.User.Id, response.User.Email, response.User.DisplayName, IsOffline: false, now));
    }

    private async Task<SignInResult> OfflineSignInAsync(LocalDbContext db, string email, string password, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.LocalUsers.SingleOrDefaultAsync(u => u.NormalizedEmail == Normalize(email), cancellationToken);
        if (user is null)
        {
            return SignInResult.Fail("The server is not reachable and this account has not signed in on this computer before. Connect once to set it up.");
        }

        if (user.IsRevoked)
        {
            return SignInResult.Fail("Access for this account has been revoked. Contact your administrator.");
        }

        if (user.LockedUntilUtc is { } locked && locked > now)
        {
            return SignInResult.Fail("Too many failed attempts. Try again later or connect to the server.");
        }

        if (now > user.OfflineAllowedUntilUtc)
        {
            return SignInResult.Fail("The offline sign-in period has expired. Connect to the server to sign in.");
        }

        var expected = protector.Unprotect(user.ProtectedVerifier, VerifierPurpose);
        if (!OfflineVerifier.Verify(password, user.VerifierSalt, expected, user.Iterations))
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= MaxOfflineFailures)
            {
                user.LockedUntilUtc = now.Add(OfflineLockout);
                user.FailedAttempts = 0;
            }

            await db.SaveChangesAsync(cancellationToken);
            return SignInResult.Fail("Invalid e-mail or password.");
        }

        user.FailedAttempts = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        session.SignIn(new SignedInUser(user.UserId, user.Email, user.DisplayName, IsOffline: true, now));
        return new SignInResult(true, SignInMode.Offline, null);
    }

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();
}
