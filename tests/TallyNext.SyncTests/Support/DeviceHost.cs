using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Infrastructure.Local;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.Infrastructure.Local.Sync;

namespace TallyNext.SyncTests.Support;

/// <summary>Simulated network between a device and the server.</summary>
public sealed class NetworkSwitch : DelegatingHandler
{
    public NetworkMode Mode { get; set; } = NetworkMode.Online;

    public int RequestsSent { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Mode == NetworkMode.Offline)
        {
            throw new HttpRequestException("Simulated network outage.");
        }

        RequestsSent++;
        var response = await base.SendAsync(request, cancellationToken);
        if (Mode == NetworkMode.LoseResponses && request.RequestUri!.AbsolutePath.Contains("/sync/push", StringComparison.Ordinal))
        {
            // The server processed the request, but the answer never reaches the device.
            response.Dispose();
            throw new HttpRequestException("Simulated connection reset after the request was sent.");
        }

        return response;
    }
}

public enum NetworkMode
{
    Online,
    Offline,
    LoseResponses,
}

/// <summary>
/// A desktop installation: its own SQLite file, the real local infrastructure and sync client, talking to
/// <see cref="ApiServer"/> through a <see cref="NetworkSwitch"/>. Can be "restarted" by disposing and reopening.
/// </summary>
public sealed class DeviceHost : IAsyncDisposable
{
    private DeviceHost(ServiceProvider services, string databaseFile, NetworkSwitch network)
    {
        Services = services;
        DatabaseFile = databaseFile;
        Network = network;
    }

    public ServiceProvider Services { get; }

    public string DatabaseFile { get; }

    public NetworkSwitch Network { get; }

    public DeviceSession Session => Services.GetRequiredService<DeviceSession>();

    public SyncEngine Engine => Services.GetRequiredService<SyncEngine>();

    public SyncStatusTracker Status => Services.GetRequiredService<SyncStatusTracker>();

    public static async Task<DeviceHost> StartAsync(ApiServer server, string? databaseFile = null, NetworkSwitch? network = null)
    {
        databaseFile ??= Path.Combine(Path.GetTempPath(), $"tallynext-device-{Guid.NewGuid():N}.db");
        network ??= new NetworkSwitch();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTallyNextLocal(o => o.DatabasePath = databaseFile);
        services.AddTallyNextDesktopSync(_ => server.CreateDefaultClient(network));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync();
        }

        return new DeviceHost(provider, databaseFile, network);
    }

    public async Task<SignInResult> SignInAsync(string email, string password = TestUsers.Password)
    {
        var result = await Services.GetRequiredService<DeviceAuthenticator>().SignInAsync(email, password, "http://localhost");
        Assert.True(result.Succeeded, result.Error);
        return result;
    }

    public Task<SyncCycleReport> SyncAsync() => Engine.SyncOnceAsync();

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

    public Task<string?> SeriesCodeAsync() => QueryAsync(db => db.DeviceRegistrations.Select(r => r.SeriesCode).SingleAsync());

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public void DeleteFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(DatabaseFile + suffix);
        }
    }
}
