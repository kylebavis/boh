using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Boh.Web.Data;

/// <summary>Applies SQLite pragmas per connection; <c>synchronous</c> would otherwise revert.</summary>
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
