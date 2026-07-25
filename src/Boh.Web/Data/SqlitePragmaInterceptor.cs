using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boh.Web.Data;

/// <summary>
/// Applies SQLite pragmas on every connection. EF Core sets none of these itself.
/// <c>journal_mode</c> is persisted in the database file so it only really takes effect
/// once, but <c>synchronous</c> is per-connection and would silently revert to FULL
/// on each new pooled connection without this.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
