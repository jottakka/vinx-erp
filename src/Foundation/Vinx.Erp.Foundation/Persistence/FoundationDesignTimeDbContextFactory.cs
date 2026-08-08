using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vinx.Erp.Foundation.Persistence.Interceptors;

namespace Vinx.Erp.Foundation.Persistence;

public sealed class FoundationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<FoundationDbContext>
{
    public FoundationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("FOUNDATION_MIGRATOR_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("FOUNDATION_MIGRATOR_CONNECTION must be set for EF design-time operations.");
        }

        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new RowVersionInterceptor())
            .Options;

        return new FoundationDbContext(options);
    }
}
