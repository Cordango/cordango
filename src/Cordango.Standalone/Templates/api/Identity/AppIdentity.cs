using System.Security.Claims;
using Cordango.Standalone.Directory;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Records;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace {{AppNamespace}}.Identity;

/// <summary>Somebody who can sign in. Extend it freely — it is your class.</summary>
public sealed class AppUser : IdentityUser
{
    /// <summary>The directory Person this login belongs to, when it belongs to one. A login and a
    /// person are separate things: most people in the directory never sign in, and somebody leaving
    /// should lose their login without erasing the approvals they signed.</summary>
    public string? PersonId { get; set; }

    public string? DisplayName { get; set; }
}

/// <summary>The sign-in tables. Kept in their own context so that upgrading ASP.NET Core Identity
/// never shows up as a migration against your own tables.</summary>
public sealed class AppIdentityDbContext : IdentityDbContext<AppUser>
{
    public AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options) : base(options) { }
}

public static class AppIdentitySetup
{
    /// <summary>
    /// Stock ASP.NET Core Identity with a cookie, and three deliberate adjustments.
    ///
    /// <para><b>Stock, on purpose.</b> This application does not share an authentication stack with
    /// anything — not with Cordango Platform, not with another generated application. Sign-in is the
    /// part of a system that is most damaging to get subtly wrong and least valuable to be clever
    /// about, so it is the framework's own implementation, which is reviewed by people whose whole
    /// job that is. Swapping it for single sign-on later means changing this file and nothing
    /// else.</para>
    /// </summary>
    public static IServiceCollection AddAppIdentity(this IServiceCollection services, bool requireHttps = false)
    {
        services
            .AddIdentity<AppUser, IdentityRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.SignIn.RequireConfirmedAccount = false;

                // Ten tries then a five-minute wait. Slow enough to make guessing pointless, short
                // enough that somebody who mistyped their own password is not locked out of their
                // afternoon.
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppIdentityDbContext>()
            .AddClaimsPrincipalFactory<AppClaimsFactory>()
            .AddDefaultTokenProviders();

        // Adjustment three: a second way in, for callers that are not browsers.
        //
        // A script, a CI job and the /mcp endpoint have no cookie and no way to echo an antiforgery
        // token. AddAccessKeyAuthentication adds a bearer scheme beside the cookie and picks between
        // them per request, so both reach the same endpoints with the same claims and the same roles.
        services.AddAccessKeyAuthentication();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "{{AppKey}}-session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Same switch as the antiforgery cookie, for the same reason: Secure over plain HTTP
            // means the browser never stores it, and the symptom is a login that appears to succeed
            // and leaves you signed out.
            options.Cookie.SecurePolicy = requireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;

            // Adjustment two: this is an API, so an unauthenticated call gets a status code rather
            // than a redirect to a login page that does not exist on the server. A 302 to
            // /Account/Login reaches a fetch() as an opaque success, and the client renders an empty
            // list instead of a sign-in prompt.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        return services;
    }
}

/// <summary>
/// Puts the directory Person id on the signed-in principal.
///
/// <para>A claim rather than a lookup, because almost every list a person opens asks the question:
/// "expenses I submitted", "tickets assigned to me". One database read at sign-in beats one on every
/// request, and the claim is refreshed whenever the principal is — including after
/// <c>RefreshSignInAsync</c>.</para>
/// </summary>
public sealed class AppClaimsFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole>
{
    public AppClaimsFactory(
        UserManager<AppUser> users,
        RoleManager<IdentityRole> roles,
        IOptions<IdentityOptions> options)
        : base(users, roles, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.PersonId is { Length: > 0 } person)
            identity.AddClaim(new Claim(HttpContextUser.PersonIdClaim, person));
        return identity;
    }
}

public static class IdentitySetup
{
    public const string AdministratorRole = "Administrator";

    /// <summary>
    /// Make sure the role exists, and create the first account only when somebody chose its
    /// password.
    ///
    /// <para><b>Nobody is ever handed a password they did not pick.</b> There were two ways to get
    /// an administrator onto an empty database and both are worse than this one. A fixed default
    /// would mean every application this toolchain produces ships with the same credentials. A
    /// GENERATED one — which this did first — has no such hole, but it can only be delivered by
    /// printing it to the log, and a first run that ends with "now go and find the password in
    /// <c>docker compose logs</c>" is a first run that does not work for anybody who arrived
    /// through a browser rather than a terminal.</para>
    ///
    /// <para>So the account is created by the person who is going to use it, on the first screen
    /// they see: <c>POST /api/account/setup</c>, open only while this application has no
    /// administrator at all. See <c>AccountController.Setup</c> for what guards it.</para>
    ///
    /// <para>Setting <c>Admin:Email</c> and <c>Admin:Password</c> keeps the unattended path: a
    /// deployment that provisions its own credentials never shows the setup screen, because by the
    /// time anybody reaches it an administrator already exists. That is the shape to use in CI and
    /// anywhere the first request will not be yours.</para>
    ///
    /// <para>It runs on every start and does nothing once an administrator exists, so it is safe to
    /// leave in place. It never resets a password.</para>
    /// </summary>
    public static async Task EnsureAdministratorAsync(IServiceProvider services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        var identity = services.GetRequiredService<AppIdentityDbContext>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySetup");

        if (!await roles.RoleExistsAsync(AdministratorRole))
            await roles.CreateAsync(new IdentityRole(AdministratorRole));

        // Any administrator at all is enough. Checking for a particular ADDRESS instead would
        // recreate the account somebody deliberately removed.
        if (await AdministratorExistsAsync(identity)) return;

        var password = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            // Deliberately shouty. Somebody watching the log needs to know that the application is
            // up, that it is waiting for them, and that it will not ask twice.
            log.LogWarning(
                "\n"
                + "========================= FIRST RUN =========================\n"
                + "  This database has no administrator yet.\n"
                + "  Open the application in a browser and it will ask you to\n"
                + "  create one. That form answers only while this is true — as\n"
                + "  soon as an administrator exists it stops.\n"
                + "  To skip it, set Admin__Email and Admin__Password instead.\n"
                + "============================================================");
            return;
        }

        var email = configuration["Admin:Email"] is { Length: > 0 } configured ? configured : "admin@localhost";
        var created = await CreateAdministratorAsync(services, email, password);

        if (created.Succeeded)
            log.LogInformation("Created the first administrator, {Email}, from configuration.", email);
        else
            log.LogError(
                "Could not create the first administrator: {Errors}",
                string.Join("; ", created.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// Does anybody hold the Administrator role?
    ///
    /// <para>Asked directly of the join table rather than through
    /// <c>UserManager.GetUsersInRoleAsync</c>, which materialises every administrator to answer a
    /// yes-or-no question. This sits on the path of every anonymous request that asks who it is, so
    /// it wants to be an indexed EXISTS and nothing more.</para>
    /// </summary>
    public static async Task<bool> AdministratorExistsAsync(AppIdentityDbContext identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var roleId = await identity.Roles
            .Where(role => role.Name == AdministratorRole)
            .Select(role => role.Id)
            .FirstOrDefaultAsync();

        return roleId is not null && await identity.UserRoles.AnyAsync(link => link.RoleId == roleId);
    }

    /// <summary>
    /// Create an administrator account, and the directory Person behind it.
    ///
    /// <para>Called from exactly two places — configuration at startup, and the setup screen — so
    /// that what an administrator IS gets decided once. The password is checked by Identity's own
    /// policy, and this returns the <see cref="IdentityResult"/> unchanged so the caller can show
    /// the real reason rather than a guess at one.</para>
    /// </summary>
    public static async Task<IdentityResult> CreateAdministratorAsync(
        IServiceProvider services,
        string email,
        string password,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();

        if (!await roles.RoleExistsAsync(AdministratorRole))
            await roles.CreateAsync(new IdentityRole(AdministratorRole));

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName is { Length: > 0 } name ? name : email,
        };

        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded) return created;

        await users.AddToRoleAsync(user, AdministratorRole);

        // A login and a directory Person are separate things, and this account needs both: the
        // definition's own screens filter on the PERSON ("expenses I submitted"), so an
        // administrator with no person record would sign in successfully and find every one of
        // those screens empty.
        await LinkPersonAsync(services, user, email);

        return IdentityResult.Success;
    }

    private static async Task LinkPersonAsync(IServiceProvider services, AppUser user, string email)
    {
        var db = services.GetRequiredService<Cordango.Standalone.Data.CordangoDbContext>();
        var users = services.GetRequiredService<UserManager<AppUser>>();

        var person = await db.Set<Person>().FirstOrDefaultAsync(p => p.Email == email);
        if (person is null)
        {
            // A readable id while one is free — "person-admin" in a foreign key column is worth
            // something when you are reading rows by hand. A second administrator created later
            // must not collide with it.
            var readable = await db.Set<Person>().AnyAsync(p => p.Id == "person-admin")
                ? "person-" + user.Id
                : "person-admin";

            person = new Person
            {
                Id = readable,
                FullName = user.DisplayName is { Length: > 0 } name ? name : email,
                Email = email,
                EmploymentStatus = "active",
                HasLogin = true,
                UserId = user.Id,
            };
            db.Add(person);
        }
        else
        {
            person.HasLogin = true;
            person.UserId = user.Id;
        }

        await db.SaveChangesAsync();

        user.PersonId = person.Id;
        await users.UpdateAsync(user);
    }
}
