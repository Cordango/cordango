using Cordango.Standalone.Directory;
using Cordango.Standalone.Http;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace {{AppNamespace}}.Identity;

public sealed record CreateUserRequest(
    string? Email,
    string? Password,
    string? DisplayName,
    string? PersonId,
    IReadOnlyList<string>? Roles);

public sealed record UpdateUserRequest(
    string? DisplayName,
    string? PersonId,
    IReadOnlyList<string>? Roles);

public sealed record ResetPasswordRequest(string? Password);

public sealed record LockRequest(bool Locked);

/// <summary>
/// Accounts, and who each of them is allowed to be.
///
/// <para><b>Why this exists.</b> Until it did, a generated application had exactly one account
/// forever: the one made on first run. There was no endpoint to add a second, none to give anybody
/// a role, and — because <c>HttpContextUser</c> reads the definition's role keys straight off the
/// principal's role claims — no way for any account but the first to hold <c>member</c> or whatever
/// else the definition declares. A second person could not have been given access even if a second
/// person could have been created.</para>
///
/// <para><b>Roles are not free text.</b> What may be assigned is exactly what the definition
/// declares, plus <c>Administrator</c>, which is the runtime's own bypass rather than a role
/// anybody wrote. Accepting an arbitrary string would create a role that grants nothing and looks
/// like it grants something, which is the worst of both.</para>
///
/// <para><b>Administrators only, and never yourself.</b> Three operations refuse to act on the
/// caller's own account — removing your own administrator role, locking yourself out, deleting
/// yourself — because each of them can leave an application nobody can administer, and the recovery
/// is a database edit.</para>
/// </summary>
[Route("api/admin/users")]
[Authorize(Roles = IdentitySetup.AdministratorRole)]
public sealed class UsersController : ControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly AppIdentityDbContext _identity;
    private readonly AppPermissions _permissions;
    private readonly IServiceProvider _services;

    public UsersController(
        UserManager<AppUser> users,
        RoleManager<IdentityRole> roles,
        AppIdentityDbContext identity,
        AppPermissions permissions,
        IServiceProvider services)
    {
        _users = users;
        _roles = roles;
        _identity = identity;
        _permissions = permissions;
        _services = services;
    }

    /// <summary>
    /// The roles that may be handed out here.
    ///
    /// <para>Read from the compiled definition rather than from the identity tables. The identity
    /// tables hold whatever has been created; the definition holds what MEANS anything. A role in
    /// the first list and not the second grants exactly nothing, and offering it in a picker is an
    /// invitation to spend an afternoon on it.</para>
    /// </summary>
    [HttpGet("/api/admin/roles")]
    public IActionResult Roles() => Ok(new
    {
        administrator = IdentitySetup.AdministratorRole,
        roles = _permissions.Roles.Select(role => new
        {
            key = role.Key,
            entities = role.Grants.Select(g => g.Entity).ToArray(),
        }),
    });

    /// <summary>Every account, with its roles and the person it belongs to.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // One query for the users, one for the role links, rather than UserManager.GetRolesAsync
        // per user — which is a round trip each and turns a page of thirty accounts into sixty-one
        // queries.
        var users = await _identity.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var roleNames = await _identity.Roles.ToDictionaryAsync(r => r.Id, r => r.Name!, ct);
        var links = await _identity.UserRoles.ToListAsync(ct);

        var byUser = links
            .GroupBy(l => l.UserId)
            .ToDictionary(
                g => g.Key,
                // OfType rather than a null check: `Where(x => x is not null)` filters correctly
                // and still hands on a string?[], so the compiler is right to complain.
                g => g.Select(l => roleNames.GetValueOrDefault(l.RoleId))
                    .OfType<string>()
                    .Order(StringComparer.Ordinal)
                    .ToArray());

        return Ok(users.Select(user => Describe(user, byUser.GetValueOrDefault(user.Id, []))));
    }

    /// <summary>
    /// Create an account, with a password the administrator chooses and the person must replace.
    ///
    /// <para><b>Why the administrator sets it.</b> The alternatives both cost something a
    /// self-hosted application does not have. An emailed invitation needs a mail server, and an
    /// application that cannot onboard anybody until SMTP is configured has made its own first
    /// step conditional on infrastructure nobody has yet — and when it is misconfigured it fails
    /// silently, which is worse than not offering it. A one-time link needs no mail server but does
    /// need a token table and an expiry policy.</para>
    ///
    /// <para>So: a password, handed over however two colleagues already talk to each other, and
    /// <see cref="AppUser.MustChangePassword"/> set so that it stops being a password two people
    /// know the first time it is used.</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var email = request?.Email?.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request?.Password))
            return BadRequest(this.Refuse("auth.credentials_required", "Enter an email address and a password."));

        if (await _users.FindByEmailAsync(email) is not null)
            return Conflict(this.Refuse("user.exists", "An account with that email address already exists."));

        if (Unassignable(request.Roles) is { } refused)
            return BadRequest(refused);

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = request.DisplayName is { Length: > 0 } name ? name.Trim() : email,
            MustChangePassword = true,
        };

        var created = await _users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            // Identity's own words, which name the rule that was broken. "Passwords must be at
            // least 12 characters" is something an administrator can act on; "invalid" is not.
            return BadRequest(this.Refuse("user.rejected", Explain(created)));

        await ApplyRolesAsync(user, request.Roles ?? []);
        await IdentitySetup.LinkPersonAsync(_services, user, email, request.PersonId);

        return Ok(Describe(user, await RolesOfAsync(user)));
    }

    /// <summary>Rename an account, move it to a different person, or change what it may do.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound(this.Refuse("user.not_found", "No account has that id."));

        if (request?.Roles is not null)
        {
            if (Unassignable(request.Roles) is { } refused) return BadRequest(refused);

            // Taking your own administrator role away is a one-way door: the endpoint that would
            // give it back is this one, and you would no longer be allowed to call it.
            if (IsSelf(user)
                && await _users.IsInRoleAsync(user, IdentitySetup.AdministratorRole)
                && !request.Roles.Contains(IdentitySetup.AdministratorRole, StringComparer.Ordinal))
            {
                return BadRequest(this.Refuse("user.self_demote",
                    "You cannot remove your own administrator role. Ask another administrator to do it."));
            }

            if (await WouldLeaveNoAdministratorAsync(user, request.Roles) is { } orphaned)
                return BadRequest(orphaned);

            await ApplyRolesAsync(user, request.Roles);
        }

        if (request?.DisplayName is { } display) user.DisplayName = display.Trim();
        if (request?.PersonId is { } person) user.PersonId = person is { Length: > 0 } ? person : null;

        var updated = await _users.UpdateAsync(user);
        if (!updated.Succeeded) return BadRequest(this.Refuse("user.rejected", Explain(updated)));

        return Ok(Describe(user, await RolesOfAsync(user)));
    }

    /// <summary>
    /// Set somebody's password for them, because they cannot get in to do it themselves.
    ///
    /// <para>Through a reset token rather than by writing the hash, so the security stamp moves and
    /// every session and access key the old password backed stops working. A reset that leaves the
    /// old sessions alive is not a reset — it is a second password.</para>
    /// </summary>
    [HttpPost("{id}/password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Password))
            return BadRequest(this.Refuse("auth.password_required", "Enter a new password."));

        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound(this.Refuse("user.not_found", "No account has that id."));

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, request.Password);
        if (!result.Succeeded) return BadRequest(this.Refuse("auth.password_rejected", Explain(result)));

        user.MustChangePassword = true;
        await _users.UpdateAsync(user);

        return NoContent();
    }

    /// <summary>
    /// Lock an account out, or let it back in.
    ///
    /// <para>A lockout rather than a deletion, because deleting somebody erases the account that
    /// created and approved things. <c>DateTimeOffset.MaxValue</c> is Identity's own idiom for
    /// "until somebody says otherwise".</para>
    /// </summary>
    [HttpPost("{id}/lock")]
    public async Task<IActionResult> Lock(string id, [FromBody] LockRequest request)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound(this.Refuse("user.not_found", "No account has that id."));

        if (request?.Locked == true && IsSelf(user))
            return BadRequest(this.Refuse("user.self_lock", "You cannot lock yourself out."));

        // Without this the lockout end date is stored and then ignored, and the account carries on
        // signing in — which reads as the button not working.
        await _users.SetLockoutEnabledAsync(user, true);
        await _users.SetLockoutEndDateAsync(user, request?.Locked == true ? DateTimeOffset.MaxValue : null);

        // A locked account's sessions and access keys are dead immediately rather than at their own
        // expiry. Locking somebody who is signed in and leaving their tab working is not locking
        // them out.
        if (request?.Locked == true) await _users.UpdateSecurityStampAsync(user);

        return NoContent();
    }

    /// <summary>
    /// Delete an account.
    ///
    /// <para>The LOGIN, not the person. The directory record stays, along with everything it
    /// created and approved — a leaver who takes their signatures with them is a hole in the
    /// history, not a tidy-up.</para>
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound(this.Refuse("user.not_found", "No account has that id."));

        if (IsSelf(user))
            return BadRequest(this.Refuse("user.self_delete", "You cannot delete your own account."));

        if (await WouldLeaveNoAdministratorAsync(user, []) is { } orphaned)
            return BadRequest(orphaned);

        // The Person keeps existing and simply stops having a login.
        var db = _services.GetRequiredService<Cordango.Standalone.Data.CordangoDbContext>();
        var person = await db.Set<Person>().FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (person is not null)
        {
            person.HasLogin = false;
            person.UserId = null;
            await db.SaveChangesAsync(ct);
        }

        var deleted = await _users.DeleteAsync(user);
        return deleted.Succeeded
            ? NoContent()
            : BadRequest(this.Refuse("user.rejected", Explain(deleted)));
    }

    // ---- the parts the endpoints share ---------------------------------------------------------

    private bool IsSelf(AppUser user) => _users.GetUserId(User) == user.Id;

    /// <summary>Every role name this application will accept, which is the definition's plus the
    /// runtime's administrator bypass.</summary>
    private HashSet<string> Assignable() =>
        [.. _permissions.Roles.Select(r => r.Key), IdentitySetup.AdministratorRole];

    private ApiError? Unassignable(IReadOnlyList<string>? requested)
    {
        if (requested is null) return null;

        var allowed = Assignable();
        var unknown = requested.Where(role => !allowed.Contains(role)).ToArray();

        return unknown.Length == 0
            ? null
            : this.Refuse("user.unknown_role",
                $"This application has no role called {string.Join(", ", unknown.Select(r => $"'{r}'"))}. "
                + $"It declares: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
    }

    /// <summary>
    /// Refuse a change that would leave the application with no administrator at all.
    ///
    /// <para>Not the same guard as <see cref="IsSelf"/>. That one stops you locking yourself out;
    /// this one stops two administrators demoting each other into a database nobody can administer.
    /// The recovery from that is a SQL prompt, so it is worth a query to avoid.</para>
    /// </summary>
    private async Task<ApiError?> WouldLeaveNoAdministratorAsync(AppUser user, IReadOnlyList<string> roles)
    {
        if (!await _users.IsInRoleAsync(user, IdentitySetup.AdministratorRole)) return null;
        if (roles.Contains(IdentitySetup.AdministratorRole, StringComparer.Ordinal)) return null;

        var administrators = await _users.GetUsersInRoleAsync(IdentitySetup.AdministratorRole);
        return administrators.Count > 1
            ? null
            : this.Refuse("user.last_administrator",
                "This is the only administrator. Give somebody else the role first, or nobody will be "
                + "able to administer this application.");
    }

    private async Task ApplyRolesAsync(AppUser user, IReadOnlyList<string> wanted)
    {
        foreach (var role in wanted)
            if (!await _roles.RoleExistsAsync(role))
                await _roles.CreateAsync(new IdentityRole(role));

        var held = await _users.GetRolesAsync(user);

        var remove = held.Except(wanted, StringComparer.Ordinal).ToArray();
        if (remove.Length > 0) await _users.RemoveFromRolesAsync(user, remove);

        var add = wanted.Except(held, StringComparer.Ordinal).ToArray();
        if (add.Length > 0) await _users.AddToRolesAsync(user, add);

        // The roles live on the principal as claims, minted at sign-in. Without this the person
        // carries their old roles until their cookie expires — up to a fortnight of access somebody
        // has already been told they no longer have.
        await _users.UpdateSecurityStampAsync(user);
    }

    private async Task<IReadOnlyList<string>> RolesOfAsync(AppUser user) =>
        [.. (await _users.GetRolesAsync(user)).Order(StringComparer.Ordinal)];

    private static string Explain(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));

    private static object Describe(AppUser user, IReadOnlyList<string> roles) => new
    {
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName ?? user.Email,
        personId = user.PersonId,
        roles,
        mustChangePassword = user.MustChangePassword,
        // A lockout in the future is a lock somebody applied. A lockout in the past is the
        // failed-attempt counter having done its job and expired, which is not the same thing and
        // must not show as a locked account.
        locked = user.LockoutEnd is { } until && until > DateTimeOffset.UtcNow,
        lockedUntil = user.LockoutEnd,
    };
}
