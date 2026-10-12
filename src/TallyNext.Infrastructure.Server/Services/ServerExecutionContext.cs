using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TallyNext.Application.Abstractions;
using TallyNext.Domain.Audit;
using TallyNext.Domain.Common;

namespace TallyNext.Infrastructure.Server.Services;

public static class TallyClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string DeviceId = "device_id";

    public static Guid? UserIdOf(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(Subject) ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public static Guid? DeviceIdOf(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(DeviceId), out var id) ? id : null;
}

/// <summary>
/// Execution context on the server. By default it reads the authenticated principal of the current HTTP request;
/// Blazor circuits (which outlive the HTTP request) set the principal explicitly per operation.
/// </summary>
public sealed class ServerExecutionContext(IHttpContextAccessor httpContextAccessor) : IExecutionContext
{
    private ClaimsPrincipal? _principal;
    private string? _correlationId;

    public ClaimsPrincipal? Principal => _principal ?? httpContextAccessor.HttpContext?.User;

    public Guid? UserId => Principal?.Identity?.IsAuthenticated == true ? TallyClaims.UserIdOf(Principal) : null;

    public string? UserEmail => Principal?.FindFirstValue(TallyClaims.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email);

    public string? UserDisplayName => Principal?.FindFirstValue(TallyClaims.Name) ?? Principal?.FindFirstValue(ClaimTypes.Name);

    public Guid? DeviceId => TallyClaims.DeviceIdOf(Principal);

    public string CorrelationId => _correlationId ??= httpContextAccessor.HttpContext?.TraceIdentifier ?? Ids.New().ToString("N");

    public AuditSource AuditSource { get; private set; } = AuditSource.Server;

    public void Use(ClaimsPrincipal principal, string? correlationId = null)
    {
        _principal = principal;
        _correlationId = correlationId ?? _correlationId;
    }

    public void UseSyncSource() => AuditSource = AuditSource.Sync;
}

/// <summary>The server numbers its own vouchers (web and API) in the "W" series.</summary>
internal sealed class ServerVoucherSeriesProvider : IVoucherSeriesProvider
{
    public Task<string> GetSeriesCodeAsync(Guid companyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Domain.Vouchers.VoucherNumber.ServerSeriesCode);
}
