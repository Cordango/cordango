using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace {{AppNamespace}}.Data;

/// <summary>
/// How `dotnet ef` builds a context without starting the application.
///
/// <para>The tools can usually find a context through the host, and "usually" is the problem: they
/// do it by running your Program up to the point where the host is built, so anything that happens
/// before that — reading a connection string, opening a file store — has to succeed on a machine
/// with no configuration, purely so that a migration can be scaffolded. This factory short-circuits
/// all of it.</para>
///
/// <para>The connection string here is used only to pick the provider and its type mappings. No
/// connection is opened to scaffold a migration, so the placeholder is harmless — set
/// <c>ConnectionStrings__Database</c> if you want `dotnet ef database update` to reach a real
/// one.</para>
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Database={{AppKey}};Username={{AppKey}};Password={{AppKey}}";

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
        return new AppDbContext(options, new AnonymousUser(), new SystemClock());
    }
}
