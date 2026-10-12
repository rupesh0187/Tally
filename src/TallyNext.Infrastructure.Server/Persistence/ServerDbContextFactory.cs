using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TallyNext.Infrastructure.Server.Persistence;

/// <summary>
/// Design-time factory used only by <c>dotnet ef</c> for the PostgreSQL (server) schema. Generating migrations does
/// not connect; <c>database update</c> uses <c>TALLYNEXT_SERVER_CONNECTION</c> when set.
/// </summary>
internal sealed class ServerDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("TALLYNEXT_SERVER_CONNECTION")
                         ?? "Host=localhost;Port=5433;Database=tallynext;Username=tallynext";
        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql(connection)
            .Options;
        return new ServerDbContext(options);
    }
}
