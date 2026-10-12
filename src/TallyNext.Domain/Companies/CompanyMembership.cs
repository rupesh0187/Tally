using TallyNext.Domain.Common;

namespace TallyNext.Domain.Companies;

/// <summary>
/// Grants a user a role in one company. Memberships are authored only by the server and replicated
/// down to devices (read-only there) so that offline authorization uses the same rules as the server.
/// (CompanyId, UserId) is unique.
/// </summary>
public sealed class CompanyMembership : CompanyScopedEntity
{
    private CompanyMembership()
    {
    }

    public Guid UserId { get; private set; }

    public CompanyRole Role { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Denormalized so devices can show member names offline.</summary>
    public string UserEmail { get; private set; } = string.Empty;

    public string? UserDisplayName { get; private set; }

    public static CompanyMembership Create(Guid companyId, Guid userId, CompanyRole role, string userEmail, string? displayName)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainException("membership.role_invalid", "The role is not valid.");
        }

        return new CompanyMembership
        {
            // Deterministic: the membership a device records offline and the one the server records are the same row.
            Id = IdFor(companyId, userId),
            CompanyId = companyId,
            UserId = userId,
            Role = role,
            IsActive = true,
            UserEmail = userEmail,
            UserDisplayName = displayName,
        };
    }

    public static Guid IdFor(Guid companyId, Guid userId) => Ids.Deterministic("tallynext/membership", companyId, userId);

    public void ChangeRole(CompanyRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainException("membership.role_invalid", "The role is not valid.");
        }

        Role = role;
        IsActive = true;
    }

    /// <summary>Revocation keeps the row (and its version) so the revocation replicates to devices.</summary>
    public void Revoke() => IsActive = false;

    public bool Allows(Permission permission) => IsActive && !IsDeleted && RolePermissions.Has(Role, permission);
}
