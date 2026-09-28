using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Boh.Web.Data;

/// <summary>For <c>dotnet ef</c> only, so tooling doesn't boot the real host.</summary>
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
