using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Domain.Companies;

namespace TallyNext.Application.Security;

/// <summary>
/// Company-scoped authorization, applied by every use case before it touches company data.
/// The same rules run on the server (memberships in PostgreSQL) and offline on a device (memberships replicated
/// from the server), so a device can never grant itself more than the server granted.
/// </summary>
public interface IAccessGuard
{
    /// <summary>
    /// Ensures the current user is an active member of <paramref name="companyId"/> with <paramref name="permission"/>.
    /// Non-members get <see cref="NotFoundException"/> (no existence leak); members lacking the permission get
    /// <see cref="ForbiddenException"/>.
    /// </summary>
    Task<CompanyMembership> RequireAsync(Guid companyId, Permission permission, CancellationToken cancellationToken = default);

    Task<bool> HasAsync(Guid companyId, Permission permission, CancellationToken cancellationToken = default);
}

internal sealed class AccessGuard(IAccountingDbContext db, IExecutionContext context) : IAccessGuard
{
    private readonly Dictionary<Guid, CompanyMembership?> _cache = [];

    public async Task<CompanyMembership> RequireAsync(Guid companyId, Permission permission, CancellationToken cancellationToken = default)
    {
        var membership = await FindAsync(companyId, cancellationToken);
        if (membership is null || !membership.IsActive || membership.IsDeleted)
        {
            throw new NotFoundException("company");
        }

        if (!RolePermissions.Has(membership.Role, permission))
        {
            throw new ForbiddenException();
        }

        return membership;
    }

    public async Task<bool> HasAsync(Guid companyId, Permission permission, CancellationToken cancellationToken = default)
    {
        var membership = await FindAsync(companyId, cancellationToken);
        return membership is not null && membership.Allows(permission);
    }

    private async Task<CompanyMembership?> FindAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var userId = context.RequireUserId();
        if (_cache.TryGetValue(companyId, out var cached))
        {
            return cached;
        }

        var membership = await db.CompanyMemberships
            .AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken);

        if (membership is not null)
        {
            var companyActive = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && !c.IsDeleted, cancellationToken);
            if (!companyActive)
            {
                membership = null;
            }
        }

        _cache[companyId] = membership;
        return membership;
    }
}
