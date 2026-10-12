using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using TallyNext.Contracts.Auth;
using TallyNext.Infrastructure.Server.Auth;
using TallyNext.SyncTests.Support;

namespace TallyNext.IntegrationTests;

public sealed class AuthenticationTests
{
    private const string Email = "auth-user@api.test";

    [Fact]
    public async Task Protected_endpoints_require_authentication()
    {
        await using var server = new ApiServer();
        var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sync/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/companies")).StatusCode);
    }

    [Fact]
    public async Task Register_login_and_me()
    {
        await using var server = new ApiServer();
        var profile = await server.RegisterAsync(Email);
        var client = await server.SignInAsync(Email);
        var me = await (await client.GetAsync("/api/auth/me")).ReadAsync<UserProfileDto>();
        Assert.Equal(profile.Id, me.Id);
        Assert.Equal(Email, me.Email);
    }

    [Fact]
    public async Task Weak_passwords_and_duplicate_emails_are_rejected_without_revealing_accounts()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Email);
        var client = server.CreateClient();

        var weak = await client.PostJsonAsync("/api/auth/register", new RegisterRequest { Email = "weak@api.test", Password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var duplicate = await client.PostJsonAsync("/api/auth/register", new RegisterRequest { Email = Email, Password = TestUsers.Password });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.DoesNotContain("already", await duplicate.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Email);
        var client = server.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostJsonAsync("/api/auth/login", new LoginRequest { Email = Email, Password = "wrong-password-xyz" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal("auth.invalid_credentials", await wrong.ProblemCodeAsync());
        }

        var locked = await client.PostJsonAsync("/api/auth/login", new LoginRequest { Email = Email, Password = TestUsers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("Too many", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var unknown = await client.PostJsonAsync("/api/auth/login", new LoginRequest { Email = "nobody@api.test", Password = TestUsers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_whole_family()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Email);
        var client = server.CreateClient();
        var first = await (await client.PostJsonAsync("/api/auth/login", new LoginRequest { Email = Email, Password = TestUsers.Password })).ReadAsync<TokenResponse>();

        var second = await (await client.PostJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = first.RefreshToken })).ReadAsync<TokenResponse>();
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // The first token was already used: presenting it again is treated as theft.
        var replay = await client.PostJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // ...which also revokes the legitimate successor.
        var successor = await client.PostJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_loses_access_immediately_even_with_an_unexpired_token()
    {
        await using var server = new ApiServer();
        var user = await server.RegisterAsync(Email);
        var client = await server.SignInAsync(Email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/companies")).StatusCode);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthService>().DisableUserAsync(user.Id, CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/companies")).StatusCode);
        var login = await server.CreateClient().PostJsonAsync("/api/auth/login", new LoginRequest { Email = Email, Password = TestUsers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Revoked_device_tokens_stop_working_and_cannot_be_refreshed()
    {
        await using var server = new ApiServer();
        await server.RegisterAsync(Email);
        var client = server.CreateClient();
        var deviceId = Guid.CreateVersion7();
        var tokens = await (await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = Email,
            Password = TestUsers.Password,
            DeviceId = deviceId,
            Device = new DeviceLoginInfo { Name = "Test PC", Platform = "windows" },
        })).ReadAsync<TokenResponse>();
        Assert.NotNull(tokens.Device);
        Assert.StartsWith("D", tokens.Device!.SeriesCode, StringComparison.Ordinal);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/devices/{deviceId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sync/companies")).StatusCode);

        var refresh = await server.CreateClient().PostJsonAsync("/api/auth/refresh", new RefreshRequest { RefreshToken = tokens.RefreshToken, DeviceId = deviceId });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("auth.access_revoked", await refresh.ProblemCodeAsync());
    }

    [Fact]
    public async Task Device_series_codes_are_unique_per_installation()
    {
        Assert.Equal("D1", DeviceService.SeriesCodeFor(1));
        Assert.Equal("DZ", DeviceService.SeriesCodeFor(35));
        Assert.Equal("D10", DeviceService.SeriesCodeFor(36));

        await using var server = new ApiServer();
        await server.RegisterAsync(Email);
        var codes = new HashSet<string>();
        for (var i = 0; i < 5; i++)
        {
            var tokens = await (await server.CreateClient().PostJsonAsync("/api/auth/login", new LoginRequest { Email = Email, Password = TestUsers.Password, DeviceId = Guid.CreateVersion7() }))
                .ReadAsync<TokenResponse>();
            Assert.True(codes.Add(tokens.Device!.SeriesCode));
        }
    }
}
