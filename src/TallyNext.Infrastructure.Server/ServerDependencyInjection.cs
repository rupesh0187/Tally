using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TallyNext.Application;
using TallyNext.Application.Abstractions;
using TallyNext.Infrastructure.Server.Auth;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.Infrastructure.Server.Services;
using TallyNext.Infrastructure.Server.Sync;

namespace TallyNext.Infrastructure.Server;

public enum ServerDatabaseProvider
{
    Postgres,

    /// <summary>Automated tests only. Refused in the Production environment.</summary>
    Sqlite,
}

public sealed class ServerDatabaseOptions
{
    public const string SectionName = "Database";

    public ServerDatabaseProvider Provider { get; set; } = ServerDatabaseProvider.Postgres;

    /// <summary>Apply migrations at startup (Development convenience; production deploys run migrations explicitly).</summary>
    public bool MigrateOnStartup { get; set; }
}

public static class ServerDependencyInjection
{
    /// <summary>Registers the server database, Identity stores, application use cases, auth and sync services.</summary>
    public static IdentityBuilder AddTallyNextServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var databaseOptions = configuration.GetSection(ServerDatabaseOptions.SectionName).Get<ServerDatabaseOptions>() ?? new ServerDatabaseOptions();
        services.AddSingleton(databaseOptions);
        var connectionString = configuration.GetConnectionString("Server");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Server is not configured. Set it with the ConnectionStrings__Server environment variable.");
        }

        if (databaseOptions.Provider == ServerDatabaseProvider.Sqlite && environment.IsProduction())
        {
            throw new InvalidOperationException("The SQLite server provider is for automated tests only and cannot be used in Production.");
        }

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ServerExecutionContext>();
        services.AddScoped<IExecutionContext>(sp => sp.GetRequiredService<ServerExecutionContext>());
        services.AddScoped<SyncOriginContext>();
        services.AddDbContext<ServerDbContext>(options =>
        {
            if (databaseOptions.Provider == ServerDatabaseProvider.Postgres)
            {
                // No retrying execution strategy: it cannot be combined with the explicit transactions that hold the
                // company write lock. Transient failures surface to the caller (devices retry sync with backoff).
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });
        services.AddScoped<IAccountingDbContext>(sp => sp.GetRequiredService<ServerDbContext>());
        services.AddScoped<IVoucherSeriesProvider, ServerVoucherSeriesProvider>();
        services.AddScoped<IUserDirectory, IdentityUserDirectory>();
        services.AddTallyNextApplication();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.AddScoped<TokenService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<AuthService>();
        services.AddScoped<SyncChangeApplier>();
        services.AddScoped<SyncPushService>();
        services.AddScoped<SyncQueryService>();
        services.AddScoped<ServerDatabaseInitializer>();
        services.AddScoped<DevelopmentSeeder>();

        return services
            .AddIdentityCore<AppUser>(ConfigureIdentity)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ServerDbContext>();
    }

    public static void ConfigureIdentity(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Length over composition (NIST SP 800-63B); Identity's PBKDF2 hasher stores only salted hashes.
        options.Password.RequiredLength = 10;
        options.Password.RequiredUniqueChars = 4;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
        options.User.RequireUniqueEmail = true;
    }
}
