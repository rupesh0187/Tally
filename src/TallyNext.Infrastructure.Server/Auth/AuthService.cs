using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TallyNext.Application.Common;
using TallyNext.Contracts.Auth;
using TallyNext.Domain.Common;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Auth;

/// <summary>Account sign-up, password sign-in (with lockout), token refresh and sign-out for API clients.</summary>
public sealed class AuthService(
    UserManager<AppUser> users,
    ServerDbContext db,
    TokenService tokens,
    DeviceService devices,
    IAuditTrail audit,
    IOptions<AuthOptions> options,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    private const string InvalidCredentials = "Invalid email or password.";

    public async Task<UserProfileDto> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Value.AllowSelfRegistration)
        {
            throw new ForbiddenException("Self-registration is disabled on this server.");
        }

        var email = request.Email.Trim();
        var user = new AppUser
        {
            Id = Ids.New(),
            UserName = email,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            // Duplicate e-mail is reported generically to avoid account enumeration.
            var errors = result.Errors
                .Where(e => e.Code is not (nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
                .Select(e => e.Description)
                .ToArray();
            throw new ValidationFailedException(
                "auth.registration_failed",
                errors.Length > 0 ? string.Join(" ", errors) : "Registration failed with these details.",
                new Dictionary<string, string[]> { [nameof(RegisterRequest.Password)] = errors });
        }

        audit.Record(null, "auth.registered", "user", user.Id, $"Account created for {email}");
        await db.SaveChangesAsync(cancellationToken);
        return new UserProfileDto(user.Id, email, user.DisplayName);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            // Equalize timing with the real password check so that response time does not reveal accounts.
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, request.Password ?? string.Empty);
            await RecordFailureAsync(null, request.Email, "unknown or disabled account", cancellationToken);
            throw new UnauthenticatedLoginException(InvalidCredentials);
        }

        if (await users.IsLockedOutAsync(user))
        {
            await RecordFailureAsync(user.Id, request.Email, "locked out", cancellationToken);
            throw new UnauthenticatedLoginException("Too many failed attempts. Try again later.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
        {
            await users.AccessFailedAsync(user);
            await RecordFailureAsync(user.Id, request.Email, "wrong password", cancellationToken);
            throw new UnauthenticatedLoginException(InvalidCredentials);
        }

        await users.ResetAccessFailedCountAsync(user);

        Device? device = null;
        if (request.DeviceId is { } deviceId)
        {
            device = await devices.EnsureRegisteredAsync(deviceId, request.Device?.Name, request.Device?.Platform, user.Id, cancellationToken);
            if (device.IsRevoked)
            {
                await RecordFailureAsync(user.Id, request.Email, "revoked device", cancellationToken);
                throw new UnauthenticatedLoginException("This device has been revoked. Contact your administrator.", "auth.access_revoked");
            }
        }

        var (access, accessExpires) = tokens.CreateAccessToken(user, device?.Id);
        var (refresh, refreshEntity) = tokens.CreateRefreshToken(user.Id, device?.Id, Ids.New());
        audit.Record(null, "auth.login", "user", user.Id, device is null ? $"{user.Email} signed in" : $"{user.Email} signed in on device {device.SeriesCode}", deviceId: device?.Id);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} signed in (device {DeviceId})", user.Id, device?.Id);

        return new TokenResponse(
            access, accessExpires, refresh, refreshEntity.ExpiresAtUtc,
            new UserProfileDto(user.Id, user.Email ?? string.Empty, user.DisplayName),
            options.Value.OfflineGraceDays,
            device is null ? null : DeviceService.ToDto(device));
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rotation = await tokens.RotateAsync(request.RefreshToken ?? string.Empty, request.DeviceId, cancellationToken);
        if (!rotation.Success)
        {
            if (rotation.Reason == "reuse_detected")
            {
                audit.Record(null, "auth.refresh_reuse_detected", "user", rotation.Current?.UserId, "A rotated refresh token was presented again; its token family was revoked.");
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            if (rotation.Reason is "device_revoked" or "user_disabled" or "account_or_device_revoked")
            {
                throw new UnauthenticatedLoginException("Access has been revoked.", "auth.access_revoked");
            }

            throw new UnauthenticatedLoginException("The session has expired. Sign in again.", "auth.session_expired");
        }

        var current = rotation.Current!;
        var user = await users.FindByIdAsync(current.UserId.ToString());
        Device? device = current.DeviceId is { } id ? await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken) : null;
        if (user is null || !user.IsActive || (current.DeviceId is not null && (device is null || device.IsRevoked)))
        {
            await tokens.RevokeFamilyAsync(current.FamilyId, "account_or_device_revoked", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthenticatedLoginException("Access has been revoked.", "auth.access_revoked");
        }

        var (access, accessExpires) = tokens.CreateAccessToken(user, device?.Id);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TokenResponse(
            access, accessExpires, rotation.NewToken!, rotation.NewEntity!.ExpiresAtUtc,
            new UserProfileDto(user.Id, user.Email ?? string.Empty, user.DisplayName),
            options.Value.OfflineGraceDays,
            device is null ? null : DeviceService.ToDto(device));
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await tokens.RevokeAsync(request.RefreshToken ?? string.Empty, "logout", cancellationToken);
    }

    /// <summary>Called for every authenticated API request: the user must still be active and the device not revoked.</summary>
    public async Task<bool> IsPrincipalStillValidAsync(Guid userId, Guid? deviceId, CancellationToken cancellationToken)
    {
        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.LockoutEnd })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null || !state.IsActive || (state.LockoutEnd is { } until && until > clock.GetUtcNow()))
        {
            return false;
        }

        return deviceId is not { } id || await devices.IsActiveAsync(id, cancellationToken);
    }

    /// <summary>Disables an account: no new sign-ins, existing tokens rejected at the next request, refresh tokens revoked.</summary>
    public async Task DisableUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("user");
        user.IsActive = false;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await tokens.RevokeForUserAsync(userId, "user_disabled", cancellationToken);
        audit.Record(null, "auth.user_disabled", "user", userId, $"{user.Email} disabled");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(Guid? userId, string? email, string reason, CancellationToken cancellationToken)
    {
        var safeEmail = email is { Length: > 254 } ? email[..254] : email;
        audit.Record(null, "auth.login_failed", "user", userId, $"Failed sign-in for '{safeEmail}': {reason}");
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Failed sign-in ({Reason}) for user {UserId}", reason, userId);
    }

    private static class DummyHash
    {
        public static readonly string Value = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));
    }
}

/// <summary>
/// Sign-in or token refresh failed (HTTP 401). Codes: <c>auth.invalid_credentials</c>, <c>auth.session_expired</c>
/// (sign in again) and <c>auth.access_revoked</c> (user disabled or device revoked).
/// </summary>
public sealed class UnauthenticatedLoginException(string message, string code = "auth.invalid_credentials") : AppException(code, message);
