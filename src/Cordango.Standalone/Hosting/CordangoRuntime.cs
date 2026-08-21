// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Hooks;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cordango.Standalone.Hosting;

/// <summary>Wiring the runtime into an application's <c>Program.cs</c>.</summary>
public static class CordangoRuntime
{
    /// <summary>
    /// Register the runtime. Called once, before the per-entity registrations.
    ///
    /// <para><b>Antiforgery is global and it is not optional.</b> A generated application
    /// authenticates the browser with a cookie, which means the browser attaches it to any request
    /// any page can provoke — so every state-changing verb needs a token the attacker's page cannot
    /// read. <see cref="AntiforgeryFilter"/> as a global filter covers POST, PUT, PATCH and DELETE
    /// across every controller, including ones added later by hand. Applying it per controller
    /// instead would work exactly until somebody adds a controller and does not know to, and that
    /// failure is silent from the inside — the endpoint works perfectly, for everybody, including
    /// the page that forged the request.</para>
    /// </summary>
    public static IServiceCollection AddCordangoRuntime(this IServiceCollection services, Action<CordangoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CordangoOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IRecordIdGenerator, GuidRecordIdGenerator>();

        // Scoped: who the caller is changes per request. Registered here so an application that has
        // not wired authentication yet still resolves — as nobody, with no roles.
        services.TryAddScoped<ICurrentUser, HttpContextUser>();

        // An application with no commands still resolves the service, and gets one that knows about
        // none. The generated AppSetup replaces this with the definition's own catalogue.
        services.TryAddSingleton(Commands.AppCommandCatalogue.Empty);
        services.TryAddScoped<Notifications.NotificationService>();

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AntiforgeryHeader;

            // The __Host- prefix, and ONLY when the application is served over HTTPS.
            //
            // The prefix is real hardening — it stops a compromised sibling subdomain writing a
            // cookie this application would then trust — and a browser enforces it by REFUSING any
            // cookie of that name without the Secure attribute. Over plain http://localhost, which
            // is what `docker compose up` gives you, that means the antiforgery cookie is silently
            // dropped and every POST fails, including the one that signs you in. The application
            // looks broken and the reason is invisible: there is no error, just a cookie that never
            // arrived.
            o.Cookie.Name = options.RequireHttps ? SecureAntiforgeryCookie : AntiforgeryCookie;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = options.RequireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
        });

        services.AddControllers(mvc =>
        {
            // Our own filter, not AutoValidateAntiforgeryTokenAttribute — see AntiforgeryFilter for
            // why the framework's own one cannot be used by an application with no views.
            mvc.Filters.Add<AntiforgeryFilter>();
        });

        return services;
    }

    /// <summary>
    /// Everything one entity needs: its descriptor, its store, and the collection its hooks join.
    ///
    /// <para>Generated, one line per entity, in definition order. Hooks register themselves next to
    /// it — <c>services.AddScoped&lt;IBeforeCreate&lt;Expense&gt;, ExpenseComputedFields&gt;()</c> —
    /// and a hand-written hook is another line in the same file, which is the point of putting hooks
    /// in the container instead of on the entity.</para>
    /// </summary>
    public static IServiceCollection AddRecord<T>(this IServiceCollection services, RecordDescriptor<T> descriptor)
        where T : class, IRecord, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton(descriptor);
        services.AddScoped<RecordHooks<T>>();
        services.AddScoped<IRecordStore<T>, RecordStore<T>>();
        services.AddScoped<Commands.CommandService<T>>();

        // Every entity can be seeded, including the directory. Registered as a collection so the
        // seed runner can ask for "all of them" without a registry to keep in step.
        services.AddScoped<ISeedTarget, SeedTarget<T>>();
        return services;
    }

    /// <summary>
    /// Put the error wire and the antiforgery cookie in the pipeline. Call it early — before
    /// routing, authentication and the endpoints — so that everything downstream is covered.
    /// </summary>
    public static IApplicationBuilder UseCordangoRuntime(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<AntiforgeryCookieMiddleware>();
        return app;
    }

    /// <summary>The request header the SPA echoes the antiforgery token in.</summary>
    public const string AntiforgeryHeader = "X-XSRF-TOKEN";

    /// <summary>The http-only cookie ASP.NET stores its half of the token pair in.</summary>
    public const string AntiforgeryCookie = "cordango-csrf";

    /// <summary>The same cookie, hardened, for an application that knows it is behind TLS. A browser
    /// refuses a <c>__Host-</c> cookie that is not <c>Secure</c>, so this name and
    /// <see cref="CordangoOptions.RequireHttps"/> travel together or not at all.</summary>
    public const string SecureAntiforgeryCookie = "__Host-cordango-csrf";

    /// <summary>The readable cookie the SPA copies into the header. Deliberately NOT http-only —
    /// that is the whole mechanism: script on this origin can read it, script on another origin
    /// cannot.</summary>
    public const string AntiforgeryClientCookie = "XSRF-TOKEN";
}

/// <summary>What an application can change about the runtime without editing it.</summary>
public sealed class CordangoOptions
{
    /// <summary>The role name that bypasses definition roles entirely. One name, and it is a real
    /// ASP.NET role rather than a definition role, so reading the definition never turns up a role
    /// that grants more than it says.</summary>
    public string AdministratorRole { get; set; } = "Administrator";

    /// <summary>
    /// This application is served over HTTPS: mark the cookies <c>Secure</c> and use the hardened
    /// <c>__Host-</c> name.
    ///
    /// <para>Off by default, because the default way to run a generated application is
    /// <c>docker compose up</c> on <c>http://localhost</c>, and switching it on there makes sign-in
    /// impossible with no error to explain why. Turn it on — <c>Security__RequireHttps=true</c> —
    /// the moment there is TLS in front, and configure forwarded headers if that TLS is terminated
    /// by a proxy, or the application will still think every request arrived over plain HTTP.</para>
    /// </summary>
    public bool RequireHttps { get; set; }
}

/// <summary>
/// Hands the SPA the token it needs.
///
/// <para>Written as a cookie rather than an endpoint the client must remember to call: the first
/// page load sets it, every fetch reads it, and there is no ordering problem to get wrong. The
/// cookie is readable by script on this origin and by nothing else, which is exactly the asymmetry
/// cross-site request forgery cannot get around — the forged request carries the session cookie the
/// browser attaches automatically, but cannot carry a header whose value it was never allowed to
/// read.</para>
///
/// <para><b>The token is minted when the response STARTS, not when the request arrives.</b> An
/// antiforgery token is bound to the identity it was issued for, and this middleware sits early in
/// the pipeline — before <c>UseAuthentication</c>, so that the error handler wraps everything.
/// Minting on the way in therefore binds every token to "nobody", and the first POST after signing
/// in fails validation against the user who is now signed in. Worse, so does the one after that:
/// each GET refreshes the cookie with another anonymous token. The application appears to sign you
/// in and then refuse everything you do.</para>
///
/// <para>Deferring to <see cref="HttpResponse.OnStarting(Func{Task})"/> puts the work after the
/// endpoint has run, which is also after <c>SignInAsync</c> has replaced
/// <see cref="HttpContext.User"/> — so the login response itself carries a token bound to the person
/// who just signed in.</para>
/// </summary>
public sealed class AntiforgeryCookieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAntiforgery _antiforgery;

    public AntiforgeryCookieMiddleware(RequestDelegate next, IAntiforgery antiforgery)
    {
        _next = next;
        _antiforgery = antiforgery;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var tokens = _antiforgery.GetAndStoreTokens(context);
            if (tokens.RequestToken is { } token)
            {
                context.Response.Cookies.Append(CordangoRuntime.AntiforgeryClientCookie, token, new CookieOptions
                {
                    // NOT http-only. That is the entire mechanism: script on this origin reads it,
                    // script on another origin cannot.
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                    Path = "/",
                });
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}

/// <summary>
/// The caller, read off the request's own principal.
///
/// <para>Reads standard claims, not Identity types, so this compiles and behaves the same whether
/// the application kept stock ASP.NET Core Identity or replaced it with single sign-on.</para>
/// </summary>
public sealed class HttpContextUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly CordangoOptions _options;

    public HttpContextUser(IHttpContextAccessor accessor, CordangoOptions options)
    {
        _accessor = accessor;
        _options = options;
    }

    private System.Security.Claims.ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public string? UserId =>
        Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    /// <summary>From a claim the application puts on the principal at sign-in, so answering it
    /// costs nothing per request. A claim rather than a lookup because it is read by almost every
    /// list a person opens.</summary>
    public string? PersonId => Principal?.FindFirst(PersonIdClaim)?.Value;

    /// <summary>The claim type carrying the directory Person id.</summary>
    public const string PersonIdClaim = "cordango:person_id";

    public IReadOnlyCollection<string> RoleKeys =>
        Principal?.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .Where(r => !string.Equals(r, _options.AdministratorRole, StringComparison.Ordinal))
            .ToArray() ?? [];

    public bool IsAdministrator => Principal?.IsInRole(_options.AdministratorRole) ?? false;
}
