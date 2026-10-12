using TallyNext.Domain.Audit;

namespace TallyNext.Application.Abstractions;

/// <summary>Who is acting, from where. Resolved per request (server) or per signed-in session (device).</summary>
public interface IExecutionContext
{
    Guid? UserId { get; }

    string? UserEmail { get; }

    string? UserDisplayName { get; }

    /// <summary>The registered device performing the action; null for browser sessions.</summary>
    Guid? DeviceId { get; }

    string CorrelationId { get; }

    /// <summary>Server for web/API actions, Device for actions taken in the desktop application.</summary>
    AuditSource AuditSource { get; }

    Guid RequireUserId() => UserId ?? throw new Common.UnauthenticatedException();
}

/// <summary>Supplies the voucher-number series code used by this store when it posts a voucher.</summary>
public interface IVoucherSeriesProvider
{
    /// <summary>"W" on the server; the device's provisioned series code on a desktop.</summary>
    Task<string> GetSeriesCodeAsync(Guid companyId, CancellationToken cancellationToken = default);
}

/// <summary>Server-side lookup of user accounts (backed by ASP.NET Core Identity). Not available offline.</summary>
public interface IUserDirectory
{
    Task<DirectoryUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<DirectoryUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record DirectoryUser(Guid Id, string Email, string? DisplayName, bool IsActive);
