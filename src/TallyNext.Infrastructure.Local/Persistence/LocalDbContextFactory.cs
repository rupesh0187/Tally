using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TallyNext.Infrastructure.Local.Persistence;

/// <summary>Design-time factory used only by <c>dotnet ef migrations</c> for the SQLite (device) schema.</summary>
internal sealed class LocalDbContextFactory : IDesignTimeDbContextFactory<LocalDbContext>
{
    public LocalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=design-time-local.db")
            .Options;
        return new LocalDbContext(options);
    }
}
