// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using Cordango.Standalone.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The boundary decides the language, on EVERY path out.
///
/// <para>It used to decide it on one. <see cref="ErrorHandlingMiddleware"/> translated everything
/// thrown below it, while every controller that RETURNED a refusal built the error by hand with an
/// English sentence baked in — so a German client got <c>record.not_found</c> in German and
/// <c>auth.required</c> in English, decided by nothing but which code path refused it. Nothing
/// failed, no test went red, and the whole translation table was half unreachable.</para>
/// </summary>
public class ApiMessageTests
{
    [Fact]
    public void A_controller_refusal_is_translated()
    {
        var controller = ControllerWith(new Table { ["auth.required"] = "Bitte zuerst anmelden." });

        var error = controller.Refuse("auth.required", "Sign in first.");

        Assert.Equal("auth.required", error.Code);
        Assert.Equal("Bitte zuerst anmelden.", error.Error);
    }

    /// <summary>
    /// A code with no entry keeps the sentence its caller wrote — and that is used deliberately.
    ///
    /// <para>Identity's password rules name the rule that was broken. A table entry for
    /// <c>setup.rejected</c> would replace "Passwords must be at least 12 characters." with a
    /// generic "that account could not be created", which is worse in every language.</para>
    /// </summary>
    [Fact]
    public void A_code_with_no_entry_keeps_the_sentence_its_caller_wrote()
    {
        var controller = ControllerWith(new Table { ["auth.required"] = "Bitte zuerst anmelden." });

        var error = controller.Refuse("setup.rejected", "Passwords must be at least 12 characters.");

        Assert.Equal("Passwords must be at least 12 characters.", error.Error);
    }

    /// <summary>An application with no messages configured answers in English rather than throwing
    /// from inside an error path, which would turn a 403 into a 500.</summary>
    [Fact]
    public void No_message_service_is_not_a_failure()
    {
        var controller = ControllerWith(messages: null);

        Assert.Equal("Sign in first.", controller.Refuse("auth.required", "Sign in first.").Error);
    }

    /// <summary>
    /// Nothing builds the wire error by hand.
    ///
    /// <para>The tripwire, and the reason the fix above stays fixed. A new endpoint written the
    /// obvious way — <c>return Unauthorized(new ApiError(…))</c> — compiles, works, and silently
    /// answers one language. Reading the source is the only way to catch that, because the
    /// behaviour is correct in the language the author was writing in.</para>
    /// </summary>
    [Fact]
    public void No_endpoint_constructs_its_own_wire_error()
    {
        // The three files that are ALLOWED to: the record itself, the helper that translates, and
        // the middleware, which has no controller to hang an extension method off.
        string[] permitted = ["ApiError.cs", "ControllerErrors.cs", "ErrorHandlingMiddleware.cs"];

        var runtime = Path.Combine(TestPaths.RepoRoot(), "src", "Cordango.Standalone");

        var offenders = System.IO.Directory
            .EnumerateFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !permitted.Contains(Path.GetFileName(path)))
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"new ApiError\("))
            .Select(path => Path.GetRelativePath(runtime, path))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These build the wire error directly and so answer in one language. Use `this.Refuse(code, fallback)`:\n  "
            + string.Join("\n  ", offenders));
    }

    private sealed class Table : Dictionary<string, string>, IApiMessages
    {
        public string Translate(string code, string fallback) => TryGetValue(code, out var m) ? m : fallback;
    }

    private sealed class Probe : ControllerBase;

    private static ControllerBase ControllerWith(IApiMessages? messages)
    {
        var services = new ServiceCollection();
        if (messages is not null) services.AddSingleton(messages);

        return new Probe
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            },
        };
    }
}
