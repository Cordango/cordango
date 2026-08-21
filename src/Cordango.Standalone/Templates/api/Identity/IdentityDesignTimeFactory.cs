using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace {{AppNamespace}}.Identity;

/// <summary>The same short-circuit for the sign-in tables. See
/// <see cref="Data.AppDbContextFactory"/>.</summary>
public sealed class AppIdentityDbContextFactory : IDesignTimeDbContextFactory<AppIdentityDbContext>
{
    public AppIdentityDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Database={{AppKey}};Username={{AppKey}};Password={{AppKey}}";

        var options = new DbContextOptionsBuilder<AppIdentityDbContext>().UseNpgsql(connection).Options;
        return new AppIdentityDbContext(options);
    }
}
