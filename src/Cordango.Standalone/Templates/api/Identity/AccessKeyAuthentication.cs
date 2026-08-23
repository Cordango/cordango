using System.Security.Claims;
using System.Text.Encodings.Web;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace {{AppNamespace}}.Identity;

/// <summary>
/// Signing in with an access key instead of a browser session.
///
/// <para>Everything else here authenticates a person at a keyboard: a cookie, and an antiforgery
/// token only a browser can echo. A script cannot hold either, and neither can an AI client on the
/// /mcp endpoint. This is the other door — <c>Authorization: Bearer cordango_pat...</c> — and it
/// leads to exactly the same room.</para>
///
/// <para><b>It mints the principal the same way a login does.</b> Not similar claims assembled by
/// hand: the actual <see cref="IUserClaimsPrincipalFactory{TUser}"/> the cookie path uses, so the
/// roles, the person id and anything added to <c>AppClaimsFactory</c> later are all present without
/// this file being told about them. Every permission check downstream then behaves identically
/// whether the caller is a browser or a program, which is the property worth having: anything that
/// had to ask "was this a key?" would be a second authorization model.</para>
/// </summary>
public sealed class AccessKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Named <c>SchemeName</c> rather than <c>Scheme</c> because the base class already has
    /// a <c>Scheme</c> — the resolved <c>AuthenticationScheme</c> for the request — and two members
    /// with one name meaning two things is how the wrong one gets used.</summary>
    public const string SchemeName = "cordango-access-key";

    /// <summary>Which scheme a request gets, decided per request. A token means this one; anything
    /// else means the cookie — so a browser and a script reach the same endpoints unchanged.</summary>
    public const string PolicyScheme = "cordango-auth";

    private readonly IAccessKeys _keys;
    private readonly UserManager<AppUser> _users;
    private readonly IUserClaimsPrincipalFactory<AppUser> _principals;

    public AccessKeyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccessKeys keys,
        UserManager<AppUser> users,
        IUserClaimsPrincipalFactory<AppUser> principals)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _users = users;
        _principals = principals;
    }

    /// <summary>The token on a request, or null. Public so the policy scheme can ask the same
    /// question without a second opinion about what a token looks like.</summary>
    public static string? TokenOn(HttpRequest request)
    {
        var header = request?.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return null;

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (TokenOn(Request) is not { } token) return AuthenticateResult.NoResult();

        var userId = await _keys.VerifyAsync(token, Context.RequestAborted);

        // Deliberately the same answer for every way a token can be wrong — unknown, expired,
        // mistyped. Distinguishing them tells whoever is trying which half they got right.
        if (userId is null) return AuthenticateResult.Fail("That access key is not valid.");

        var user = await _users.FindByIdAsync(userId);
        if (user is null) return AuthenticateResult.Fail("That access key is not valid.");

        // A key belonging to a locked-out account is a key that stops working, without anybody
        // having to remember to revoke it alongside the lockout.
        if (await _users.IsLockedOutAsync(user)) return AuthenticateResult.Fail("That access key is not valid.");

        var principal = await _principals.CreateAsync(user);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

public static class AccessKeyAuthentication
{
    /// <summary>
    /// Put the token door beside the cookie one.
    ///
    /// <para>A POLICY scheme rather than two default schemes, because "which credential is this
    /// request carrying" has to be answered per request. Making the bearer handler the default would
    /// break every browser call; making the cookie the default would ignore the token.</para>
    /// </summary>
    public static IServiceCollection AddAccessKeyAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = AccessKeyHandler.PolicyScheme;
                options.DefaultChallengeScheme = AccessKeyHandler.PolicyScheme;
            })
            .AddPolicyScheme(AccessKeyHandler.PolicyScheme, AccessKeyHandler.PolicyScheme, options =>
                options.ForwardDefaultSelector = context =>
                    AccessKeyHandler.TokenOn(context.Request) is not null
                        ? AccessKeyHandler.SchemeName
                        : IdentityConstants.ApplicationScheme)
            .AddScheme<AuthenticationSchemeOptions, AccessKeyHandler>(AccessKeyHandler.SchemeName, null);

        return services;
    }
}
