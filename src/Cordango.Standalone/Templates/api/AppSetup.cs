using Cordango.Standalone.Security;

namespace {{AppNamespace}};

/// <summary>
/// {{AppName}}'s entities, hooks and permissions, registered.
///
/// <para>One place, one line per thing, in the order the definition lists them — which is why a
/// generated application starts up the same way twice and why <c>grep</c> can answer what it
/// registers. Regenerating replaces this file; put your own registrations in a file beside it and
/// call it from <c>Program.cs</c>.</para>
/// </summary>
public static class AppSetup
{
    public static IServiceCollection AddApp(this IServiceCollection services)
    {
        // The definition's roles, compiled in. Empty until entities are generated, which — with
        // nobody signed in getting nothing regardless — means a freshly scaffolded application
        // gives an authenticated caller read-only access to the directory and nothing else.
        services.AddSingleton(AppPermissions.None);

        // Entities are registered here, one AddRecord<T> call each, with their hooks beside them.
        // Generated.

        return services;
    }
}
