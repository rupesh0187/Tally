namespace TallyNext.Contracts.Auth;

public sealed class RegisterRequest
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

public sealed class LoginRequest
{
    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Required for desktop clients: binds the tokens to this installation (registered on first login).</summary>
    public Guid? DeviceId { get; set; }

    public DeviceLoginInfo? Device { get; set; }
}

public sealed class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;

    public Guid? DeviceId { get; set; }
}

public sealed class LogoutRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed record UserProfileDto(Guid Id, string Email, string? DisplayName);

/// <param name="OfflineGraceDays">How long a device may authenticate this user offline after the last online check.</param>
/// <param name="Device">The registered device when the login was made from a desktop installation.</param>
public sealed record TokenResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserProfileDto User,
    int OfflineGraceDays,
    DeviceDto? Device = null);

/// <summary>Device details sent with a desktop login; the device is registered on first use.</summary>
public sealed class DeviceLoginInfo
{
    public string Name { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;
}

/// <param name="SeriesCode">Globally unique voucher-number series code for vouchers created on this device.</param>
public sealed record DeviceDto(
    Guid Id,
    string Name,
    string Platform,
    string SeriesCode,
    DateTime RegisteredAtUtc,
    DateTime? LastSeenAtUtc,
    bool IsRevoked);
