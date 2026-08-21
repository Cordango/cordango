// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Http;

/// <summary>
/// Every state-changing request carries an antiforgery token, on every controller, without anybody
/// opting in.
///
/// <para><b>Why not <c>AutoValidateAntiforgeryTokenAttribute</c>.</b> That is the framework's own
/// answer and it was the first thing tried. It is a filter FACTORY that resolves
/// <c>AutoValidateAntiforgeryTokenAuthorizationFilter</c> from the container — a service registered
/// by <c>AddControllersWithViews</c> and <c>AddRazorPages</c>, and NOT by <c>AddControllers</c>.
/// A generated application is an API and registers the third, so the attribute was accepted at
/// startup and threw on the first request that reached any action: "No service for type … has been
/// registered", from inside the filter pipeline, on a plain GET to <c>/health</c>. Registering
/// ViewFeatures to get one filter would be adding a view engine to an application with no views.</para>
///
/// <para>So this does the same job in fifteen lines with no hidden dependency: skip the safe verbs,
/// honour anything marked to be ignored, and hand the rest to <see cref="IAntiforgery"/>. The
/// failure it raises is <see cref="AntiforgeryValidationException"/>, which
/// <see cref="ErrorHandlingMiddleware"/> already answers as <c>request.csrf_invalid</c> rather than
/// as a server error.</para>
/// </summary>
public sealed class AntiforgeryFilter : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var method = context.HttpContext.Request.Method;

        // The safe verbs, as the HTTP specification defines them. A GET must not change anything, so
        // requiring a token on one would only break links.
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
            return;

        // An endpoint may opt out — a webhook receiver authenticated by signature has no session
        // cookie to forge and no way to carry a token. [IgnoreAntiforgeryToken] says so.
        foreach (var metadata in context.ActionDescriptor.EndpointMetadata)
            if (metadata is IAntiforgeryMetadata { RequiresValidation: false })
                return;

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        await antiforgery.ValidateRequestAsync(context.HttpContext);
    }
}
