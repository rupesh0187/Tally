using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TallyNext.Domain.Common;
using TallyNext.Infrastructure.Local.Session;

namespace TallyNext.Infrastructure.Local.Persistence;

/// <summary>
/// Startup routine for the desktop database: applies SQLite migrations, creates this installation's device id on
/// first run and recovers outbox entries that were in flight when the application last stopped.
/// </summary>
public sealed class LocalDatabaseInitializer(LocalDbContext db, DeviceSession session, ILogger<LocalDatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(db.Database.GetDbConnection().DataSource);
        if (!string.IsNullOrEmpty(directory) && !string.Equals(directory, ":memory:", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.MigrateAsync(cancellationToken);

        var registration = await db.DeviceRegistrations.SingleOrDefaultAsync(cancellationToken);
        if (registration is null)
        {
            registration = new DeviceRegistration
            {
                Id = DeviceRegistration.SingletonId,
                DeviceId = Ids.New(),
                DeviceName = Environment.MachineName,
            };
            db.DeviceRegistrations.Add(registration);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created device identity {DeviceId}", registration.DeviceId);
        }

        session.DeviceId = registration.DeviceId;

        // Anything in flight when the app stopped is re-sent; the server deduplicates by change id.
        var recovered = await db.OutboxEntries
            .Where(e => e.Status == OutboxStatus.InFlight)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, OutboxStatus.Pending), cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning("Recovered {Count} outbox entries that were in flight at shutdown", recovered);
        }
    }
}
