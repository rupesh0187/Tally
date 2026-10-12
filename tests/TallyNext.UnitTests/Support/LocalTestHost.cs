using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Infrastructure.Local;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Session;

namespace TallyNext.UnitTests.Support;

/// <summary>
/// A desktop-like host over a real SQLite database (in-memory by default, or a file to test restarts).
/// Each <see cref="RunAsync{TService,TResult}"/> call uses a fresh DI scope, exactly as the UI does.
/// </summary>
public sealed class LocalTestHost : IAsyncDisposable
{
    private readonly SqliteConnection? _keepAlive;

    private LocalTestHost(ServiceProvider services, SqliteConnection? keepAlive)
    {
        Services = services;
        _keepAlive = keepAlive;
        Session = services.GetRequiredService<DeviceSession>();
    }

    public ServiceProvider Services { get; }

    public DeviceSession Session { get; }

    public Guid UserId => Session.User!.UserId;

    public static async Task<LocalTestHost> CreateAsync(string? databaseFile = null, string seriesCode = "D1", Guid? userId = null, TimeProvider? clock = null)
    {
        SqliteConnection? keepAlive = null;
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        if (databaseFile is null)
        {
            keepAlive = new SqliteConnection("Data Source=:memory:");
            await keepAlive.OpenAsync();
            await using (var pragma = keepAlive.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync();
            }

            var connection = keepAlive;
            services.AddTallyNextLocalCore(o => o.UseSqlite(connection));
        }
        else
        {
            services.AddTallyNextLocal(o => o.DatabasePath = databaseFile);
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var host = new LocalTestHost(provider, keepAlive);

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            var registration = await db.DeviceRegistrations.SingleAsync();
            registration.SeriesCode ??= seriesCode;
            await db.SaveChangesAsync();
        }

        host.Session.SignIn(new SignedInUser(userId ?? Guid.CreateVersion7(), "owner@example.test", "Test Owner", IsOffline: true, DateTime.UtcNow));
        return host;
    }

    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task<TResult> QueryAsync<TResult>(Func<LocalDbContext, Task<TResult>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<LocalDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
    }
}
