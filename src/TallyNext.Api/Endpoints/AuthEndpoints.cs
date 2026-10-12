using System.Security.Claims;
using TallyNext.Contracts.Auth;
using TallyNext.Infrastructure.Server.Auth;
using TallyNext.Infrastructure.Server.Services;

namespace TallyNext.Api.Endpoints;

internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Authentication");

        auth.MapPost("/register", async (RegisterRequest request, AuthService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RegisterAsync(request, ct)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Create an account (when self-registration is enabled).");

        auth.MapPost("/login", async (LoginRequest request, AuthService service, CancellationToken ct) =>
                TypedResults.Ok(await service.LoginAsync(request, ct)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Sign in with e-mail and password. Desktop clients pass their device id.");

        auth.MapPost("/refresh", async (RefreshRequest request, AuthService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RefreshAsync(request, ct)))
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Exchange a refresh token for new tokens (rotation with reuse detection).");

        auth.MapPost("/logout", async (LogoutRequest request, AuthService service, CancellationToken ct) =>
            {
                await service.LogoutAsync(request, ct);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Revoke a refresh token.");

        auth.MapGet("/me", (ClaimsPrincipal user) => TypedResults.Ok(new UserProfileDto(
                TallyClaims.UserIdOf(user) ?? Guid.Empty,
                user.FindFirstValue(TallyClaims.Email) ?? string.Empty,
                user.FindFirstValue(TallyClaims.Name))))
            .WithSummary("The signed-in user.");

        var devices = app.MapGroup("/api/devices").WithTags("Devices");
        devices.MapGet("/", async (ClaimsPrincipal user, DeviceService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListForUserAsync(TallyClaims.UserIdOf(user)!.Value, ct)))
            .WithSummary("Devices registered by the signed-in user.");
        devices.MapPost("/{deviceId:guid}/revoke", async (Guid deviceId, ClaimsPrincipal user, DeviceService service, CancellationToken ct) =>
            {
                await service.RevokeAsync(deviceId, TallyClaims.UserIdOf(user)!.Value, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Revoke a device: its tokens stop working and it can no longer upload changes.");
    }
}
