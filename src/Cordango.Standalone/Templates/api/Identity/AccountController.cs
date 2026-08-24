using Cordango.Standalone.Http;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace {{AppNamespace}}.Identity;

public sealed record LoginRequest(string? Email, string? Password, bool RememberMe = false);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>A new access key. The label is for the person's own list; the expiry is optional, and a
/// key without one has to be revoked by hand.</summary>
public sealed record MintKeyRequest(string? Label, DateTimeOffset? Expires);

/// <summary>What the first-run screen sends: the account the person setting this up wants to
/// have.</summary>
public sealed record SetupRequest(string? Email, string? Password, string? DisplayName);

/// <summary>
/// Signing in, signing out, asking who you are — and, exactly once in the life of a database,
/// creating the account that everything else needs.
///
/// <para>Every route that changes something is a POST, and every POST in this application requires
/// the antiforgery token — including these. That is not ceremony: the sign-in cookie is attached by
/// the browser to any request any page can provoke, so without a token that only this origin can
/// read, another site could sign somebody into an account of its choosing and watch what they do
/// next.</para>
/// </summary>
[Route("api/account")]
public sealed class AccountController : ControllerBase
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly AppIdentityDbContext _identity;
    private readonly IServiceProvider _services;

    /// <summary>
    /// One setup at a time, per process.
    ///
    /// <para>Without it, submitting the first-run form twice in quick succession — a double click,
    /// or a client that retries — runs two check-then-creates that both see an empty database and
    /// both succeed, leaving two administrators where the person asked for one. The gate closes
    /// that. It does NOT close the same race across two instances of this application starting
    /// against one database, which would need a lock in Postgres; that is a real hole and a
    /// deliberately unpatched one, because a deployment running several instances is a deployment
    /// that should be setting <c>Admin__Email</c> and <c>Admin__Password</c> and never seeing this
    /// endpoint at all.</para>
    /// </summary>
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    public AccountController(
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        AppIdentityDbContext identity,
        IServiceProvider services)
    {
        _signIn = signIn;
        _users = users;
        _identity = identity;
        _services = services;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(this.Refuse("auth.credentials_required", "Enter an email address and a password."));

        var result = await _signIn.PasswordSignInAsync(
            request.Email, request.Password, request.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
            return StatusCode(423, this.Refuse("auth.locked_out", "Too many attempts. Try again in a few minutes."));

        // One answer for "no such account" and for "wrong password", deliberately. Two different
        // answers turn the login form into a way to find out who has an account here.
        if (!result.Succeeded)
            return Unauthorized(this.Refuse("auth.invalid_credentials", "That email address and password do not match."));

        var user = await _users.FindByNameAsync(request.Email);
        return Ok(await Describe(user!));
    }

    /// <summary>
    /// Create the first administrator, from the browser, on a database that has none.
    ///
    /// <para><b>Why this is anonymous, and why that is not a hole.</b> It answers only while this
    /// application has no administrator whatsoever, which is true for the seconds between a fresh
    /// database coming up and the person who started it opening the page. From the moment an account
    /// exists it returns 409 to everyone, forever, and no configuration can reopen it — only
    /// deleting every administrator can, which is a deliberate act and is also the recovery path
    /// when somebody loses their only account.</para>
    ///
    /// <para>The window is real, and it is the same one Grafana, Jellyfin and every other
    /// self-hosted application has. If the first request to reach this application will not be
    /// yours — anything exposed to the internet the moment it starts — set <c>Admin__Email</c> and
    /// <c>Admin__Password</c>, and the account exists before the first packet arrives.</para>
    /// </summary>
    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup([FromBody] SetupRequest request)
    {
        var email = request?.Email?.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request?.Password))
            return BadRequest(this.Refuse("auth.credentials_required", "Enter an email address and a password."));

        await SetupGate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            if (await IdentitySetup.AdministratorExistsAsync(_identity))
                return Conflict(this.Refuse("setup.completed",
                    "This application already has an administrator. Sign in instead."));

            var created = await IdentitySetup.CreateAdministratorAsync(
                _services, email, request.Password, request.DisplayName?.Trim());

            if (!created.Succeeded)
                // Identity's own words, which name the rule that was broken — "Passwords must be at
                // least 12 characters" is something a person can act on, and a generic "invalid" is
                // not. `setup.rejected` is deliberately ABSENT from Resources/messages.*.json for
                // exactly that reason: a code with no entry keeps the sentence written here.
                return BadRequest(this.Refuse("setup.rejected",
                    string.Join(" ", created.Errors.Select(e => e.Description))));
        }
        finally
        {
            SetupGate.Release();
        }

        // Signed in on the spot. Making somebody type the password they chose four seconds ago into
        // a login form proves nothing and reads as a bug.
        var user = await _users.FindByNameAsync(email);
        await _signIn.SignInAsync(user!, isPersistent: false);
        return Ok(await Describe(user!));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return NoContent();
    }

    /// <summary>Who the caller is. The one call the SPA makes before rendering anything, so that a
    /// reload lands on the page a signed-in person expects rather than on the login form — and, when
    /// nobody is signed in, whether this database has been set up at all.</summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<IActionResult> Me()
    {
        // `setupRequired` is only ever computed for a caller who is not signed in. Somebody holding
        // a session is proof enough that an account exists, and this way the query does not run on
        // the request every page of the application makes.
        if (User.Identity?.IsAuthenticated != true)
            return Ok(new
            {
                authenticated = false,
                setupRequired = !await IdentitySetup.AdministratorExistsAsync(_identity),
            });

        var user = await _users.GetUserAsync(User);
        if (user is null) return Ok(new { authenticated = false, setupRequired = false });

        return Ok(await Describe(user));
    }

    [HttpPost("password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest(this.Refuse("auth.password_required", "Enter your current password and a new one."));

        var user = await _users.GetUserAsync(User);
        if (user is null) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(this.Refuse("auth.password_rejected",
                string.Join(" ", result.Errors.Select(e => e.Description))));

        // Whatever they were handed, they have now replaced. Clearing this is what lets them past
        // the change-password screen the router holds them on.
        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await _users.UpdateAsync(user);
        }

        // The old cookie was minted against the old password stamp. Refreshing it here keeps the
        // person signed in on this device instead of bouncing them to the login form they just
        // proved themselves at.
        await _signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    /// <summary>
    /// This person's access keys.
    ///
    /// <para>Never anybody else's, and never a secret: what comes back is the label, the dates and
    /// the id — enough to decide which one to revoke and nothing that could be used to sign in.</para>
    /// </summary>
    [HttpGet("keys")]
    [Authorize]
    public async Task<IActionResult> Keys([FromServices] IAccessKeys keys, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        var mine = await keys.ListAsync(user.Id, ct);

        return Ok(mine.Select(k => new
        {
            id = k.Id,
            label = k.Label,
            created = k.Created,
            lastUsed = k.LastUsed,
            expires = k.Expires,
        }));
    }

    /// <summary>
    /// Mint one, for a script, a CI job or an AI client on /mcp.
    ///
    /// <para><b>The token comes back once and is never recoverable.</b> Only its hash is stored, so
    /// there is no second chance to copy it and no way for anyone reading the database to use it.
    /// A key acts as its owner: it reaches exactly what this person reaches through the screens, and
    /// it stops working the moment their account is locked.</para>
    /// </summary>
    [HttpPost("keys")]
    [Authorize]
    public async Task<IActionResult> MintKey(
        [FromBody] MintKeyRequest request, [FromServices] IAccessKeys keys, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        var (key, token) = await keys.MintAsync(user.Id, request?.Label ?? "", request?.Expires, ct);

        return Ok(new
        {
            id = key.Id,
            label = key.Label,
            created = key.Created,
            expires = key.Expires,
            token,
            notice = "Copy this now. It is not stored and cannot be shown again.",
        });
    }

    [HttpDelete("keys/{id}")]
    [Authorize]
    public async Task<IActionResult> RevokeKey(string id, [FromServices] IAccessKeys keys, CancellationToken ct)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        // The same answer whether the key was somebody else's or never existed. Either way it is not
        // yours, and saying which would let somebody test ids against other people's keys.
        return await keys.RevokeAsync(user.Id, id, ct)
            ? NoContent()
            : NotFound(this.Refuse("key.not_found", "No access key of yours has that id."));
    }

    private async Task<object> Describe(AppUser user) => new
    {
        authenticated = true,
        setupRequired = false,
        id = user.Id,
        email = user.Email,
        displayName = user.DisplayName ?? user.Email,
        personId = user.PersonId,
        roles = await _users.GetRolesAsync(user),
        // The client holds them on the change-password screen while this is true. The SERVER does
        // not refuse anything on it: an account that cannot read its own record cannot be told why
        // it is being asked to change a password, and a person locked out of a form they are
        // required to complete has no way forward at all.
        mustChangePassword = user.MustChangePassword,
        isAdministrator = await _users.IsInRoleAsync(user, IdentitySetup.AdministratorRole),
    };
}
