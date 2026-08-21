// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cordango.Standalone.Http;

/// <summary>
/// Turns anything thrown below it into the <see cref="ApiError"/> wire, and turns anything else into
/// a 500 that says nothing.
///
/// <para><b>Two rules, and the second one is the point.</b> A <see cref="RecordException"/> is a
/// decision the application made, so its code and message go to the caller as written. Anything else
/// is a bug: it is logged in full, with the trace identifier, and the caller gets
/// <c>{"code":"server.error"}</c> and nothing more. Exception text is written for whoever is holding
/// the stack trace, and handing it to an anonymous client is how internal paths, table names and
/// connection strings end up in a bug report somebody else files.</para>
/// </summary>
public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _log;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context);
        }
        catch (RecordException ex)
        {
            await Write(context, ex.StatusCode, ex.ToApiError(Translate(context, ex.Code, ex.Message)));
        }
        catch (AntiforgeryValidationException ex)
        {
            // Its own case rather than a 500: a missing or stale token is a routine thing that
            // happens to a tab left open overnight, and the client can fix it by re-reading the
            // token. Telling it "server error" would send it to look in the wrong place.
            _log.LogWarning(ex, "Antiforgery validation failed for {Method} {Path}.",
                context.Request.Method, context.Request.Path);
            await Write(context, StatusCodes.Status400BadRequest, Error(context, "request.csrf_invalid",
                "Your session token was missing or out of date. Reload and try again."));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled exception for {Method} {Path}. Trace {TraceId}.",
                context.Request.Method, context.Request.Path, context.TraceIdentifier);
            await Write(context, StatusCodes.Status500InternalServerError, Error(context, "server.error",
                "Something went wrong. The failure has been logged."));
        }
    }

    /// <summary>Resolved per request, not per application: which language to answer in is a fact
    /// about the caller. Optional, so an application that has not set up translations still gets its
    /// English fallback rather than a missing-service exception thrown from the error handler
    /// itself.</summary>
    private static string Translate(HttpContext context, string code, string fallback) =>
        context.RequestServices.GetService(typeof(IApiMessages)) is IApiMessages messages
            ? messages.Translate(code, fallback)
            : fallback;

    private static ApiError Error(HttpContext context, string code, string fallback) =>
        new(code, Translate(context, code, fallback));

    private static async Task Write(HttpContext context, int status, ApiError body)
    {
        // A handler that has already started writing cannot be given a different status, and trying
        // throws a second exception on top of the first. Let the original one surface in the log.
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
