using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Boh.Web.Data;

/// <summary>
/// Used only by <c>dotnet ef</c>. Without it the tooling would boot the real host, which
/// creates the data directory and applies migrations — neither of which is wanted, or
/// even permitted, on a developer machine where BOH_DATA_PATH points at /data.
/// The connection string here is never opened for scaffolding.
/// </summary>
public sealed class BohDbContextFactory : IDesignTimeDbContextFactory<BohDbContext>
{
    public BohDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BohDbContext>()
            .UseSqlite("Data Source=boh-design.db")
            .Options;

        return new BohDbContext(options);
    }
}
