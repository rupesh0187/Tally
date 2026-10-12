using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TallyNext.Application;
using TallyNext.Application.Abstractions;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Security;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.Infrastructure.Local.Sync;

namespace TallyNext.Infrastructure.Local;

public sealed class LocalDatabaseOptions
{
    /// <summary>Full path of the SQLite file. Defaults to %LOCALAPPDATA%\TallyNext\tallynext.db.</summary>
    public string DatabasePath { get; set; } = DefaultDatabasePath();

    public static string DefaultDatabasePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TallyNext", "tallynext.db");

    public string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();
}

public static class LocalDependencyInjection
{
    /// <summary>Registers the device database, session, application use cases and offline infrastructure.</summary>
    public static IServiceCollection AddTallyNextLocal(this IServiceCollection services, Action<LocalDatabaseOptions>? configure = null)
    {
        var options = new LocalDatabaseOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        return services.AddTallyNextLocalCore(o => o.UseSqlite(options.BuildConnectionString()));
    }

    /// <summary>Registration with a caller-supplied provider configuration (used by tests with in-memory SQLite).</summary>
    public static IServiceCollection AddTallyNextLocalCore(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb)
    {
        ArgumentNullException.ThrowIfNull(configureDb);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DeviceSession>();
        services.AddScoped<IExecutionContext, DeviceExecutionContext>();
        services.AddDbContext<LocalDbContext>(o =>
        {
            configureDb(o);
            o.AddInterceptors(new SqlitePragmaInterceptor());
        });
        services.AddScoped<IAccountingDbContext>(sp => sp.GetRequiredService<LocalDbContext>());
        services.AddScoped<IVoucherSeriesProvider, DeviceSeriesProvider>();
        services.AddScoped<LocalDatabaseInitializer>();
        services.AddTallyNextApplication();
        return services;
    }

    /// <summary>
    /// Registers sign-in, token storage and synchronization for the desktop. <paramref name="httpClient"/> overrides
    /// the HTTP client (tests route it to an in-memory server); <paramref name="protector"/> overrides DPAPI.
    /// </summary>
    public static IServiceCollection AddTallyNextDesktopSync(
        this IServiceCollection services,
        Func<IServiceProvider, HttpClient>? httpClient = null,
        ISecretProtector? protector = null)
    {
        services.TryAddSingleton<ServerEndpoint>();
        if (protector is not null)
        {
            services.AddSingleton(protector);
        }
        else if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
        }
        else
        {
            throw new PlatformNotSupportedException("Secret protection is implemented with Windows DPAPI; supply an ISecretProtector on other platforms.");
        }

        services.TryAddSingleton<DeviceTokenStore>();
        services.TryAddSingleton<ISyncTransport>(sp => new HttpSyncTransport(
            httpClient?.Invoke(sp) ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                Timeout = TimeSpan.FromSeconds(60),
            },
            sp.GetRequiredService<ServerEndpoint>(),
            sp.GetRequiredService<DeviceTokenStore>()));
        services.TryAddSingleton<SyncStatusTracker>();
        services.TryAddSingleton<SyncEngine>();
        services.TryAddSingleton<SyncScheduler>();
        services.TryAddSingleton<DeviceAuthenticator>();
        services.AddScoped<ConflictResolver>();
        return services;
    }
}
