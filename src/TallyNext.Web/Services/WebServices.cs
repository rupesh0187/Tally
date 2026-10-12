using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TallyNext.Application.Common;
using TallyNext.Contracts.Sync;
using TallyNext.Domain.Companies;
using TallyNext.Infrastructure.Server.Auth;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.Infrastructure.Server.Services;
using TallyNext.Infrastructure.Server.Sync;
using TallyNext.UI.Services;

namespace TallyNext.Web.Services;

/// <summary>
/// Runs each use case in its own DI scope as the circuit's user, after confirming the account is still active
/// (a disabled user cannot make further changes even inside an open browser session).
/// </summary>
internal sealed class WebUseCaseRunner(IServiceScopeFactory scopes, AuthenticationStateProvider authentication) : IUseCaseRunner
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = await CreateScopeAsync();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = await CreateScopeAsync();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<AsyncServiceScope> CreateScopeAsync()
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        var userId = principal.Identity?.IsAuthenticated == true ? TallyClaims.UserIdOf(principal) : null;
        if (userId is null)
        {
            throw new UnauthenticatedException();
        }

        var scope = scopes.CreateAsyncScope();
        try
        {
            if (!await scope.ServiceProvider.GetRequiredService<AuthService>().IsPrincipalStillValidAsync(userId.Value, null, CancellationToken.None))
            {
                throw new UnauthenticatedException();
            }

            scope.ServiceProvider.GetRequiredService<ServerExecutionContext>().Use(principal);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }
}

internal sealed class WebHostFeatures(NavigationManager navigation) : IHostFeatures
{
    public bool IsDesktop => false;

    public bool CanManageMembers => true;

    public Task SignOutAsync()
    {
        navigation.NavigateTo("account/logout", forceLoad: true);
        return Task.CompletedTask;
    }
}

/// <summary>In the browser everything is already on the server; this panel only reviews device conflicts.</summary>
internal sealed class WebSyncPanel(IUseCaseRunner runner, AppState state) : ISyncPanel
{
    public event Action? Changed;

    public SyncPanelStatus Status { get; } = new(SyncPanelState.Online, 0, 0, null, "Working directly on the server");

    public bool CanSyncNow => false;

    public Task SyncNowAsync() => Task.CompletedTask;

    public Task RefreshAsync()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SyncIssue>> IssuesAsync(Guid companyId)
    {
        if (!state.Can(Permission.SyncConflictResolve))
        {
            return [];
        }

        var conflicts = await runner.RunAsync<SyncQueryService, IReadOnlyList<SyncConflictDto>>(s => s.ConflictsAsync(companyId, SyncConflictStatus.Open));
        return conflicts.Select(c => new SyncIssue(c.Id, c.EntityType, c.EntityId, "Conflict", c.Reason, "A device changed a record that had changed here as well.", c.ClientPayloadJson, c.ServerPayloadJson, c.CreatedAtUtc)).ToList();
    }

    public Task<IReadOnlyList<UnsyncedChange>> UnsyncedAsync(Guid companyId) => Task.FromResult<IReadOnlyList<UnsyncedChange>>([]);

    public Task ResolveAsync(Guid companyId, Guid issueId, bool keepServerVersion) =>
        runner.RunAsync<SyncQueryService, SyncConflictDto>(s => s.ResolveConflictAsync(companyId, issueId, new ResolveConflictRequest
        {
            Resolution = keepServerVersion ? SyncConflictStatus.ResolvedKeepServer : SyncConflictStatus.Dismissed,
            Note = keepServerVersion ? "Server version confirmed in the web application." : "Dismissed in the web application.",
        }));
}

/// <summary>Adds the display name claim so audit records and the UI can show it.</summary>
internal sealed class AppUserClaimsPrincipalFactory(UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            identity.AddClaim(new Claim(TallyClaims.Name, user.DisplayName));
        }

        return identity;
    }
}

/// <summary>Re-checks every minute that the signed-in user is still active; a disabled user's circuit is signed out.</summary>
internal sealed class RevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var userId = TallyClaims.UserIdOf(authenticationState.User);
        if (userId is null)
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthService>().IsPrincipalStillValidAsync(userId.Value, null, cancellationToken);
    }
}

/// <summary>Password sign-in for the browser with lockout, disabled-account check and an audit record.</summary>
internal sealed class WebSignInService(SignInManager<AppUser> signIns, UserManager<AppUser> users, IAuditTrail audit, ServerDbContext db, ILogger<WebSignInService> logger)
{
    public async Task<(bool Succeeded, string? Error)> SignInAsync(string email, string password, bool remember)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive)
        {
            await RecordAsync("auth.login_failed", user?.Id, $"Failed web sign-in for '{email}'");
            return (false, "Invalid e-mail or password.");
        }

        var result = await signIns.PasswordSignInAsync(user, password, remember, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            await RecordAsync("auth.login", user.Id, $"{user.Email} signed in to the web application");
            return (true, null);
        }

        await RecordAsync("auth.login_failed", user.Id, result.IsLockedOut ? $"Locked-out web sign-in for '{email}'" : $"Failed web sign-in for '{email}'");
        return (false, result.IsLockedOut ? "Too many failed attempts. Try again later." : "Invalid e-mail or password.");
    }

    private async Task RecordAsync(string action, Guid? userId, string summary)
    {
        audit.Record(null, action, "user", userId, summary);
        await db.SaveChangesAsync();
        logger.LogInformation("{Action} for user {UserId}", action, userId);
    }
}
