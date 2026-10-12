using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TallyNext.Infrastructure.Server.Persistence;

/// <summary>
/// Startup database preparation. PostgreSQL: applies EF migrations when enabled. SQLite (tests only): creates the
/// schema from the model, because the migrations in this project are PostgreSQL migrations.
/// </summary>
public sealed class ServerDatabaseInitializer(ServerDbContext db, ServerDatabaseOptions options, ILogger<ServerDatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (options.Provider == ServerDatabaseProvider.Sqlite)
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return;
        }

        if (!options.MigrateOnStartup)
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogWarning("{Count} database migrations are pending ({Migrations}). Apply them before serving traffic.", pending.Count, string.Join(", ", pending));
            }

            return;
        }

        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database migrations applied");
    }
}
