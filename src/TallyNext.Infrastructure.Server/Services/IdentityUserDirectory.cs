using Microsoft.AspNetCore.Identity;
using TallyNext.Application.Abstractions;
using TallyNext.Infrastructure.Server.Persistence;

namespace TallyNext.Infrastructure.Server.Services;

internal sealed class IdentityUserDirectory(UserManager<AppUser> users) : IUserDirectory
{
    public async Task<DirectoryUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(email);
        return user is null ? null : new DirectoryUser(user.Id, user.Email ?? email, user.DisplayName, user.IsActive);
    }

    public async Task<DirectoryUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? null : new DirectoryUser(user.Id, user.Email ?? string.Empty, user.DisplayName, user.IsActive);
    }
}
