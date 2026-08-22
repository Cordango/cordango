// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cordango.Standalone.Http;

/// <summary>
/// The one way a controller builds a refusal, so that a refusal is answered in the caller's
/// language.
///
/// <para><b>Why this exists.</b> The doctrine is that the code travels and the sentence is decided at
/// the boundary — and it was only half true. <see cref="ErrorHandlingMiddleware"/> translated
/// everything THROWN below it, while every controller that RETURNED an error built the
/// <see cref="ApiError"/> by hand with an English sentence in it. So a German client got
/// <c>record.not_found</c> in German and <c>auth.required</c> in English, decided by nothing more
/// than which code path refused it. One helper closes that: the same lookup, reachable from a
/// controller.</para>
///
/// <para><b>A code with no entry keeps the sentence its caller wrote</b>, and that is a feature used
/// deliberately. Identity's password rules — "Passwords must be at least 12 characters." — name the
/// rule that was broken, and a table entry for <c>setup.rejected</c> would replace them with a
/// generic "that account could not be created". Where the specific sentence is worth more than the
/// translated one, the code is left out of the table on purpose.</para>
/// </summary>
public static class ControllerErrors
{
    /// <summary>The wire error for this refusal, translated if this application has a message for the
    /// code.</summary>
    /// <param name="controller">The controller refusing. Only its <c>HttpContext</c> is used — the
    /// language is a fact about the request.</param>
    /// <param name="code">The stable dotted code, such as <c>auth.required</c>.</param>
    /// <param name="fallback">The English sentence to answer with when the code has no entry.</param>
    /// <param name="fields">Named when the refusal is about particular fields, so a form can mark
    /// them. This is also where DETAIL belongs once a sentence is translated: a generic message plus
    /// the field name beats an interpolated English sentence a German reader cannot read.</param>
    public static ApiError Refuse(
        this ControllerBase controller,
        string code,
        string fallback,
        IReadOnlyList<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return new ApiError(code, Translate(controller.HttpContext, code, fallback), fields);
    }

    /// <summary>
    /// The message for this code in the caller's language.
    ///
    /// <para>Resolved per request, not per application: which language to answer in is a fact about
    /// the caller. Optional, so an application that has not set up translations still gets its
    /// English fallback rather than a missing-service exception thrown from an error path.</para>
    /// </summary>
    internal static string Translate(HttpContext? context, string code, string fallback) =>
        context?.RequestServices.GetService(typeof(IApiMessages)) is IApiMessages messages
            ? messages.Translate(code, fallback)
            : fallback;
}
