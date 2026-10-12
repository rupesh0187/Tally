using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TallyNext.Infrastructure.Local.Persistence;

/// <summary>
/// Durability settings applied to every SQLite connection of the desktop database:
/// WAL journaling (readers never block the writer), FULL synchronous mode (a committed voucher survives a power
/// loss), enforced foreign keys and a busy timeout so concurrent UI and sync writes wait instead of failing.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
