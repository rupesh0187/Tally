using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.SyncTests.Support;

namespace TallyNext.SyncTests;

public sealed class OfflineAuthenticationTests
{
    private const string User = "offline@auth.test";

    [Fact]
    public async Task First_sign_in_requires_the_server_then_offline_sign_in_works()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        await using var device = await DeviceHost.StartAsync(server);
        var auth = device.Services.GetRequiredService<DeviceAuthenticator>();

        device.Network.Mode = NetworkMode.Offline;
        var never = await auth.SignInAsync(User, TestUsers.Password, "http://localhost");
        Assert.False(never.Succeeded);

        device.Network.Mode = NetworkMode.Online;
        Assert.Equal(SignInMode.Online, (await device.SignInAsync(User)).Mode);
        var stored = await device.QueryAsync(db => db.LocalUsers.SingleAsync());
        Assert.NotEmpty(stored.ProtectedVerifier);
        Assert.DoesNotContain(TestUsers.Password, System.Text.Encoding.UTF8.GetString(stored.ProtectedVerifier), StringComparison.Ordinal);
        Assert.False(await device.QueryAsync(db => db.ProtectedSecrets.AnyAsync(s => s.ProtectedValue.Length == 0)));

        auth.SignOut();
        device.Network.Mode = NetworkMode.Offline;
        var offline = await auth.SignInAsync(User, TestUsers.Password);
        Assert.True(offline.Succeeded, offline.Error);
        Assert.Equal(SignInMode.Offline, offline.Mode);
        Assert.True(device.Session.User!.IsOffline);
    }

    [Fact]
    public async Task Offline_sign_in_locks_after_repeated_failures_and_expires_after_the_grace_period()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(User);
        var auth = device.Services.GetRequiredService<DeviceAuthenticator>();
        auth.SignOut();
        device.Network.Mode = NetworkMode.Offline;

        for (var i = 0; i < 5; i++)
        {
            Assert.False((await auth.SignInAsync(User, "wrong-password-123")).Succeeded);
        }

        var locked = await auth.SignInAsync(User, TestUsers.Password);
        Assert.False(locked.Succeeded);
        Assert.Contains("Too many", locked.Error, StringComparison.Ordinal);

        await device.QueryAsync(db => db.LocalUsers.ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockedUntilUtc, (DateTime?)null)
            .SetProperty(u => u.OfflineAllowedUntilUtc, DateTime.UtcNow.AddMinutes(-1))));
        var expired = await auth.SignInAsync(User, TestUsers.Password);
        Assert.False(expired.Succeeded);
        Assert.Contains("expired", expired.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_online_never_falls_back_to_offline()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        await using var device = await DeviceHost.StartAsync(server);
        await device.SignInAsync(User);
        var auth = device.Services.GetRequiredService<DeviceAuthenticator>();
        auth.SignOut();

        var result = await auth.SignInAsync(User, "not-the-password");
        Assert.False(result.Succeeded);
        Assert.Null(device.Session.User);
    }

    [Fact]
    public async Task Offline_sign_in_can_resume_synchronization_with_the_stored_refresh_token()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(User);
        var file = Path.Combine(Path.GetTempPath(), $"tallynext-auth-{Guid.NewGuid():N}.db");
        var device = await DeviceHost.StartAsync(server, file);
        await device.SignInAsync(User);
        await device.DisposeAsync();

        var network = new NetworkSwitch { Mode = NetworkMode.Offline };
        var restarted = await DeviceHost.StartAsync(server, file, network);
        try
        {
            Assert.Equal(SignInMode.Offline, (await restarted.SignInAsync(User)).Mode);
            network.Mode = NetworkMode.Online;
            await restarted.SyncAsync();
            Assert.Equal(SyncState.UpToDate, restarted.Status.Current.State);
        }
        finally
        {
            await restarted.DisposeAsync();
            restarted.DeleteFiles();
        }
    }
}
