using Cordango.Standalone.Data;
using Cordango.Standalone.Directory;
using Cordango.Standalone.Preferences;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace {{AppNamespace}}.Data;

/// <summary>
/// Everything {{AppName}} stores.
///
/// <para>The list below is the whole model. There is no assembly scan and no convention that finds
/// entities behind your back, so this file answers "what is in the database" by being read.</para>
/// </summary>
public sealed class AppDbContext : CordangoDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser user, IClock clock)
        : base(options, user, clock) { }

    protected override void ConfigureModel(ModelBuilder builder)
    {
        // People, departments, groups, organizations and contacts. Every application gets these:
        // they are what a reference to a person or a customer points at.
        builder.AddDirectory();

        // Each person's own column layouts, keyed by who they are. Never shared.
        builder.AddPreferences();

        // {{AppName}}'s own entities are applied here, one line each, in definition order.
        // Generated — regenerating replaces this file.
    }
}
