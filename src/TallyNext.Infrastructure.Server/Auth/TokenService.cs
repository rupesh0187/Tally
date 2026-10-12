using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TallyNext.Domain.Common;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.Infrastructure.Server.Services;

namespace TallyNext.Infrastructure.Server.Auth;

/// <summary>Issues short-lived JWT access tokens and rotating, hashed refresh tokens.</summary>
public sealed class TokenService(ServerDbContext db, IOptions<JwtOptions> options, TimeProvider clock)
{
    public static SymmetricSecurityKey CreateSigningKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }

        return new SymmetricSecurityKey(bytes);
    }

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user, Guid? deviceId)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var claims = new Dictionary<string, object>
        {
            [TallyClaims.Subject] = user.Id.ToString(),
            [TallyClaims.Email] = user.Email ?? string.Empty,
            [JwtRegisteredClaimNames.Jti] = Ids.New().ToString("N"),
        };
        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            claims[TallyClaims.Name] = user.DisplayName;
        }

        if (deviceId is { } device)
        {
            claims[TallyClaims.DeviceId] = device.ToString();
        }

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(CreateSigningKey(settings.SigningKey), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    /// <summary>Creates a refresh token (returned once in clear text; only its hash is stored).</summary>
    public (string Token, RefreshToken Entity) CreateRefreshToken(Guid userId, Guid? deviceId, Guid familyId)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var entity = new RefreshToken
        {
            Id = Ids.New(),
            UserId = userId,
            DeviceId = deviceId,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(options.Value.RefreshTokenDays),
        };
        db.RefreshTokens.Add(entity);
        return (raw, entity);
    }

    public sealed record RotationResult(bool Success, string? Reason, RefreshToken? Current, string? NewToken, RefreshToken? NewEntity);

    /// <summary>
    /// Exchanges a refresh token for a new one. Presenting a token that was already rotated or revoked is treated as
    /// theft: the whole token family is revoked.
    /// </summary>
    public async Task<RotationResult> RotateAsync(string presented, Guid? deviceId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(presented);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current is null)
        {
            return new RotationResult(false, "unknown", null, null, null);
        }

        if (current.RevokedAtUtc is not null)
        {
            if (current.RevokedReason != "rotated")
            {
                // Explicitly revoked (logout, device revoked, user disabled): report why, nothing else to do.
                return new RotationResult(false, current.RevokedReason ?? "revoked", current, null, null);
            }

            // A token that was already exchanged is being presented again: assume theft, revoke the family.
            await RevokeFamilyAsync(current.FamilyId, "reuse_detected", cancellationToken);
            return new RotationResult(false, "reuse_detected", current, null, null);
        }

        if (current.ExpiresAtUtc <= now)
        {
            return new RotationResult(false, "expired", current, null, null);
        }

        if (current.DeviceId != deviceId)
        {
            return new RotationResult(false, "device_mismatch", current, null, null);
        }

        var (token, entity) = CreateRefreshToken(current.UserId, current.DeviceId, current.FamilyId);
        current.RevokedAtUtc = now;
        current.RevokedReason = "rotated";
        current.ReplacedByTokenId = entity.Id;
        return new RotationResult(true, null, current, token, entity);
    }

    public async Task RevokeAsync(string presented, string reason, CancellationToken cancellationToken)
    {
        var hash = Hash(presented);
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    public async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    public async Task RevokeForDeviceAsync(Guid deviceId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.DeviceId == deviceId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    public async Task RevokeForUserAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
