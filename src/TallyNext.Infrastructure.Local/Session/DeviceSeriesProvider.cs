using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Infrastructure.Local.Persistence;

namespace TallyNext.Infrastructure.Local.Session;

/// <summary>
/// Vouchers posted on this device are numbered in the device's own series, assigned by the server when the
/// device was provisioned. Because each series belongs to exactly one device, offline numbering cannot collide.
/// </summary>
internal sealed class DeviceSeriesProvider(LocalDbContext db) : IVoucherSeriesProvider
{
    public async Task<string> GetSeriesCodeAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var registration = await db.DeviceRegistrations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return registration?.SeriesCode
               ?? throw new InvalidOperationException("This device has not been provisioned yet: sign in online once to obtain its voucher number series.");
    }
}
