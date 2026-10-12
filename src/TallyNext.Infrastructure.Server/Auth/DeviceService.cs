using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Common;
using TallyNext.Contracts.Auth;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Auth;

/// <summary>Registration and revocation of desktop installations.</summary>
public sealed class DeviceService(ServerDbContext db, TokenService tokens, IAuditTrail audit, TimeProvider clock)
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Returns the device, registering it (with a new globally unique voucher series code) on first use.
    /// </summary>
    public async Task<Device> EnsureRegisteredAsync(Guid deviceId, string? name, string? platform, Guid userId, CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ValidationFailedException("device.id_required", "A device id is required.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is not null)
        {
            device.LastSeenAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return device;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO \"DeviceSeriesCounters\" (\"Id\", \"LastValue\") VALUES ({DeviceSeriesCounter.SingletonId}, {0L}) ON CONFLICT DO NOTHING",
            cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE \"DeviceSeriesCounters\" SET \"LastValue\" = \"LastValue\" + 1 WHERE \"Id\" = {DeviceSeriesCounter.SingletonId}",
            cancellationToken);
        var value = await db.DeviceSeriesCounters.AsNoTracking().Where(c => c.Id == DeviceSeriesCounter.SingletonId).Select(c => c.LastValue).SingleAsync(cancellationToken);

        device = new Device
        {
            Id = deviceId,
            Name = Truncate(string.IsNullOrWhiteSpace(name) ? "Desktop" : name.Trim(), 100),
            Platform = Truncate(string.IsNullOrWhiteSpace(platform) ? "unknown" : platform.Trim(), 50),
            SeriesCode = SeriesCodeFor(value),
            RegisteredByUserId = userId,
            RegisteredAtUtc = now,
            LastSeenAtUtc = now,
        };
        db.Devices.Add(device);
        audit.Record(null, "device.registered", "device", device.Id, $"Device '{device.Name}' registered with series {device.SeriesCode}");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return device;
    }

    public async Task<IReadOnlyList<DeviceDto>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Devices.AsNoTracking()
            .Where(d => d.RegisteredByUserId == userId)
            .OrderByDescending(d => d.LastSeenAtUtc)
            .Select(d => ToDto(d))
            .ToListAsync(cancellationToken);

    /// <summary>Revokes a device registered by the caller: its tokens stop working and it can no longer upload.</summary>
    public async Task RevokeAsync(Guid deviceId, Guid userId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId && d.RegisteredByUserId == userId, cancellationToken)
            ?? throw new NotFoundException("device");
        if (device.IsRevoked)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        device.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        device.RevokedByUserId = userId;
        await tokens.RevokeForDeviceAsync(deviceId, "device_revoked", cancellationToken);
        audit.Record(null, "device.revoked", "device", device.Id, $"Device '{device.Name}' ({device.SeriesCode}) revoked");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsActiveAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.Devices.AsNoTracking().AnyAsync(d => d.Id == deviceId && d.RevokedAtUtc == null, cancellationToken);

    public static DeviceDto ToDto(Device d) => new(d.Id, d.Name, d.Platform, d.SeriesCode, d.RegisteredAtUtc, d.LastSeenAtUtc, d.RevokedAtUtc != null);

    /// <summary>"D" followed by the counter in base 36: D1, D2, ..., DZ, D10, ... (never "W", the server series).</summary>
    internal static string SeriesCodeFor(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(Alphabet[(int)(value % 36)]);
            value /= 36;
        }

        return "D" + new string([.. chars]);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
