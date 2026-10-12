using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TallyNext.Application.Common;
using TallyNext.Application.Companies;
using TallyNext.Contracts.Companies;
using TallyNext.Infrastructure.Local;
using TallyNext.Infrastructure.Local.Persistence;
using TallyNext.Infrastructure.Local.Session;
using TallyNext.Infrastructure.Local.Sync;
using TallyNext.UI;
using TallyNext.UI.Services;

namespace TallyNext.Desktop.Core;

/// <summary>Runs each use case in a fresh scope; the user comes from the device session.</summary>
internal sealed class DesktopUseCaseRunner(IServiceScopeFactory scopes, DeviceSession session) : IUseCaseRunner
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        EnsureSignedIn();
        await using var scope = scopes.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        EnsureSignedIn();
        await using var scope = scopes.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private void EnsureSignedIn()
    {
        if (session.User is null)
        {
            throw new UnauthenticatedException();
        }
    }
}

internal sealed class DesktopHostFeatures(DeviceAuthenticator authenticator, AppState state, SyncScheduler scheduler) : IHostFeatures
{
    public bool IsDesktop => true;

    public bool CanManageMembers => false;

    public async Task SignOutAsync()
    {
        await scheduler.StopAsync();
        state.SelectCompany(null);
        state.SetUser(null);
        authenticator.SignOut();
    }
}

/// <summary>Desktop sign-in for the shared UI: online first, offline fallback; starts background sync.</summary>
internal sealed class DesktopSignIn : IDesktopSignIn, IDisposable
{
    private readonly DeviceAuthenticator _authenticator;
    private readonly DeviceSession _session;
    private readonly AppState _state;
    private readonly IUseCaseRunner _runner;
    private readonly SyncScheduler _scheduler;
    private readonly SyncEngine _engine;
    private readonly ServerEndpoint _endpoint;

    public DesktopSignIn(
        DeviceAuthenticator authenticator,
        DeviceSession session,
        AppState state,
        IUseCaseRunner runner,
        SyncScheduler scheduler,
        SyncEngine engine,
        ServerEndpoint endpoint)
    {
        _authenticator = authenticator;
        _session = session;
        _state = state;
        _runner = runner;
        _scheduler = scheduler;
        _engine = engine;
        _endpoint = endpoint;
        _session.Changed += OnSessionChanged;
    }

    public event Action? Changed;

    public bool IsSignedIn => _session.User is not null;

    public string? DefaultServerUrl => _endpoint.BaseAddress?.ToString().TrimEnd('/');

    public async Task<DesktopSignInResult> SignInAsync(string email, string password, string? serverUrl)
    {
        var result = await _authenticator.SignInAsync(email, password, serverUrl);
        if (!result.Succeeded)
        {
            return new DesktopSignInResult(false, false, result.Error);
        }

        _state.SetUser(_session.User?.DisplayName ?? _session.User?.Email);
        _scheduler.Start();
        if (result.Mode == SignInMode.Online)
        {
            // First download before showing companies, so a newly provisioned device is usable immediately.
            try
            {
                await _engine.SyncOnceAsync();
            }
            catch (SyncTransportException)
            {
                // Status is shown in the UI; the scheduler retries.
            }
        }
        else
        {
            await _engine.RefreshStatusAsync();
        }

        var companies = await _runner.RunAsync<CompanyService, IReadOnlyList<CompanySummaryDto>>(s => s.ListMineAsync());
        if (companies.Count == 1)
        {
            _state.SelectCompany(companies[0]);
        }

        Changed?.Invoke();
        return new DesktopSignInResult(true, result.Mode == SignInMode.Offline, null);
    }

    public void Dispose() => _session.Changed -= OnSessionChanged;

    private void OnSessionChanged() => Changed?.Invoke();
}

/// <summary>The sync screen's view of the desktop sync engine.</summary>
internal sealed class DesktopSyncPanel : ISyncPanel, IDisposable
{
    private readonly SyncStatusTracker _tracker;
    private readonly SyncEngine _engine;
    private readonly IServiceScopeFactory _scopes;
    private readonly LedgerCache _ledgers;
    private readonly StockItemCache _items;

    public DesktopSyncPanel(SyncStatusTracker tracker, SyncEngine engine, IServiceScopeFactory scopes, LedgerCache ledgers, StockItemCache items)
    {
        _tracker = tracker;
        _engine = engine;
        _scopes = scopes;
        _ledgers = ledgers;
        _items = items;
        _tracker.Changed += OnTrackerChanged;
    }

    public event Action? Changed;

    public SyncPanelStatus Status => Map(_tracker.Current);

    public bool CanSyncNow => true;

    public async Task SyncNowAsync()
    {
        try
        {
            var report = await _engine.SyncOnceAsync();
            if (report.Downloaded > 0)
            {
                _ledgers.Invalidate();
                _items.Invalidate();
            }
        }
        catch (SyncTransportException ex)
        {
            throw new SyncUnavailableException(ex.Message);
        }
    }

    public Task RefreshAsync() => _engine.RefreshStatusAsync();

    public async Task<IReadOnlyList<SyncIssue>> IssuesAsync(Guid companyId)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var conflicts = await scope.ServiceProvider.GetRequiredService<ConflictResolver>().ListAsync(companyId);
        return conflicts.Select(c => new SyncIssue(c.Id, c.EntityType, c.EntityId, c.Kind, c.ErrorCode, c.Message, c.LocalPayloadJson, c.ServerPayloadJson, c.DetectedAtUtc)).ToList();
    }

    public async Task<IReadOnlyList<UnsyncedChange>> UnsyncedAsync(Guid companyId)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredService<ConflictResolver>().ListUnsyncedAsync(companyId);
        return pending.Select(p => new UnsyncedChange(p.LocalSequence, p.EntityType, p.EntityId, p.Status.ToString(), p.Attempts, p.LastErrorCode, p.ClientModifiedAtUtc)).ToList();
    }

    public async Task ResolveAsync(Guid companyId, Guid issueId, bool keepServerVersion)
    {
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<ConflictResolver>();
            try
            {
                if (keepServerVersion)
                {
                    await resolver.KeepServerVersionAsync(companyId, issueId);
                }
                else
                {
                    await resolver.KeepLocalVersionAsync(companyId, issueId);
                }
            }
            catch (SyncTransportException ex)
            {
                throw new SyncUnavailableException(ex.Message);
            }
        }

        _ledgers.Invalidate();
        _items.Invalidate();
        await _engine.RefreshStatusAsync();
    }

    public void Dispose() => _tracker.Changed -= OnTrackerChanged;

    private void OnTrackerChanged(SyncStatusSnapshot snapshot) => Changed?.Invoke();

    private static SyncPanelStatus Map(SyncStatusSnapshot s) => new(
        s.State switch
        {
            SyncState.Offline => SyncPanelState.Offline,
            SyncState.Syncing => SyncPanelState.Syncing,
            SyncState.UpToDate => SyncPanelState.UpToDate,
            SyncState.PendingChanges => SyncPanelState.Pending,
            SyncState.NeedsAttention => SyncPanelState.NeedsAttention,
            SyncState.SignInRequired => SyncPanelState.SignInRequired,
            SyncState.UpdateRequired => SyncPanelState.UpdateRequired,
            SyncState.NotSignedIn => SyncPanelState.Offline,
            _ => SyncPanelState.Error,
        },
        s.PendingCount,
        s.AttentionCount,
        s.LastSuccessUtc,
        s.Message);
}

/// <summary>Synchronization could not run (server unreachable, sign-in needed). Safe to show to the user.</summary>
public sealed class SyncUnavailableException(string message) : AppException("sync.unavailable", message);

/// <summary>Application start-up: database migration, device identity and the remembered server address.</summary>
public sealed class DesktopStartup(IServiceScopeFactory scopes, ServerEndpoint endpoint, ILogger<DesktopStartup> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LocalDatabaseInitializer>().InitializeAsync(cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var serverUrl = await db.DeviceRegistrations.AsNoTracking().Select(r => r.ServerUrl).SingleOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            try
            {
                endpoint.Set(serverUrl);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "The stored server address is not valid");
            }
        }
    }
}

public static class DesktopServiceCollectionExtensions
{
    /// <summary>Everything the desktop shell needs: local database, sync, shared UI and the adapters above.</summary>
    public static IServiceCollection AddTallyNextDesktop(
        this IServiceCollection services,
        Action<LocalDatabaseOptions>? database = null,
        Func<IServiceProvider, HttpClient>? httpClient = null,
        Infrastructure.Local.Security.ISecretProtector? protector = null)
    {
        services.AddTallyNextLocal(database);
        services.AddTallyNextDesktopSync(httpClient, protector);
        services.AddTallyNextUi(ServiceLifetime.Singleton);
        services.AddSingleton<IUseCaseRunner, DesktopUseCaseRunner>();
        services.AddSingleton<IHostFeatures, DesktopHostFeatures>();
        services.AddSingleton<IDesktopSignIn, DesktopSignIn>();
        services.AddSingleton<ISyncPanel, DesktopSyncPanel>();
        services.AddSingleton<DesktopStartup>();
        return services;
    }
}
