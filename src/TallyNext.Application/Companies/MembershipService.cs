using Microsoft.EntityFrameworkCore;
using TallyNext.Application.Abstractions;
using TallyNext.Application.Common;
using TallyNext.Application.Security;
using TallyNext.Application.Sync;
using TallyNext.Contracts.Companies;
using TallyNext.Domain.Companies;

namespace TallyNext.Application.Companies;

/// <summary>
/// Company membership management. Listing works everywhere (memberships replicate to devices);
/// changes require the server-side user directory and are therefore online-only.
/// </summary>
public sealed class MembershipService(
    IAccountingDbContext db,
    IAccessGuard guard,
    IAuditTrail audit,
    IUserDirectory? directory = null)
{
    public async Task<IReadOnlyList<MemberDto>> ListAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.CompanyView, cancellationToken);
        return await db.CompanyMemberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId && !m.IsDeleted)
            .OrderBy(m => m.Role).ThenBy(m => m.UserEmail)
            .Select(m => new MemberDto(m.Id, m.UserId, m.UserEmail, m.UserDisplayName, m.Role, m.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<MemberDto> AddAsync(Guid companyId, AddMemberRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await guard.RequireAsync(companyId, Permission.MemberManage, cancellationToken);
        var users = directory ?? throw new ForbiddenException("Members can only be managed while connected to the server.");
        if (!Enum.IsDefined(request.Role))
        {
            throw new ValidationFailedException("membership.role_invalid", "Choose a valid role.");
        }

        var user = await users.FindByEmailAsync(request.Email.Trim(), cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new NotFoundException("user");
        }

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var membership = await db.CompanyMemberships.SingleOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == user.Id, cancellationToken);
        var before = membership is null ? null : SnapshotMapper.ToSnapshot(membership);
        if (membership is null)
        {
            membership = CompanyMembership.Create(companyId, user.Id, request.Role, user.Email, user.DisplayName);
            db.CompanyMemberships.Add(membership);
        }
        else
        {
            if (membership.IsActive && membership.Role == CompanyRole.Owner && request.Role != CompanyRole.Owner)
            {
                await EnsureAnotherOwnerAsync(companyId, membership.Id, cancellationToken);
            }

            membership.ChangeRole(request.Role);
        }

        audit.Record(companyId, "member.added", "membership", membership.Id, $"{user.Email} added as {request.Role}", before, SnapshotMapper.ToSnapshot(membership));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new MemberDto(membership.Id, membership.UserId, membership.UserEmail, membership.UserDisplayName, membership.Role, membership.IsActive);
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid companyId, Guid membershipId, CompanyRole role, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MemberManage, cancellationToken);
        if (!Enum.IsDefined(role))
        {
            throw new ValidationFailedException("membership.role_invalid", "Choose a valid role.");
        }

        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var membership = await LoadAsync(companyId, membershipId, cancellationToken);
        if (membership.Role == CompanyRole.Owner && role != CompanyRole.Owner)
        {
            await EnsureAnotherOwnerAsync(companyId, membership.Id, cancellationToken);
        }

        var before = SnapshotMapper.ToSnapshot(membership);
        membership.ChangeRole(role);
        audit.Record(companyId, "member.role_changed", "membership", membership.Id, $"{membership.UserEmail} is now {role}", before, SnapshotMapper.ToSnapshot(membership));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new MemberDto(membership.Id, membership.UserId, membership.UserEmail, membership.UserDisplayName, membership.Role, membership.IsActive);
    }

    /// <summary>
    /// Revokes access. The server refuses the user's requests for this company immediately; devices learn of the
    /// revocation on their next sync (see SECURITY.md, "Offline revocation window").
    /// </summary>
    public async Task RevokeAsync(Guid companyId, Guid membershipId, CancellationToken cancellationToken = default)
    {
        await guard.RequireAsync(companyId, Permission.MemberManage, cancellationToken);
        await using var scope = await db.BeginCompanyWriteAsync(companyId, cancellationToken);
        var membership = await LoadAsync(companyId, membershipId, cancellationToken);
        if (membership.Role == CompanyRole.Owner && membership.IsActive)
        {
            await EnsureAnotherOwnerAsync(companyId, membership.Id, cancellationToken);
        }

        var before = SnapshotMapper.ToSnapshot(membership);
        membership.Revoke();
        audit.Record(companyId, "member.revoked", "membership", membership.Id, $"{membership.UserEmail} access revoked", before, SnapshotMapper.ToSnapshot(membership));
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    private async Task<CompanyMembership> LoadAsync(Guid companyId, Guid membershipId, CancellationToken cancellationToken) =>
        await db.CompanyMemberships.SingleOrDefaultAsync(m => m.CompanyId == companyId && m.Id == membershipId && !m.IsDeleted, cancellationToken)
        ?? throw new NotFoundException("membership");

    private async Task EnsureAnotherOwnerAsync(Guid companyId, Guid excludingMembershipId, CancellationToken cancellationToken)
    {
        var otherOwners = await db.CompanyMemberships.AnyAsync(
            m => m.CompanyId == companyId && m.Id != excludingMembershipId && m.Role == CompanyRole.Owner && m.IsActive && !m.IsDeleted,
            cancellationToken);
        if (!otherOwners)
        {
            throw new ConflictException("membership.last_owner", "A company must keep at least one active Owner.");
        }
    }
}
