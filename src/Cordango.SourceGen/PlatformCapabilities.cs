// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.SourceGen;

/// <summary>
/// What Cordango Platform cannot run, which is almost nothing.
///
/// <para><b>Deliberately not an <see cref="IAppSourceGenerator"/>.</b> The platform produces no
/// source: it has no emitters, no capability declaration and no output directory, and the whole
/// reason <c>--target platform</c> is a special case is that a generator's questions do not apply to
/// it. Making it implement the interface to get one refusal would put a thing that generates nothing
/// into the registry of things that generate, and every test that walks that registry would then
/// have to special-case it back out.</para>
///
/// <para><b>Why there is anything here at all.</b> The platform runs an application by interpreting
/// its definition. There is no compiler in the runtime and no sandbox, so a method somebody wrote in
/// C# cannot be executed there — and the failure is silent rather than loud: the evaluator would
/// answer unknown, the column would be blank, and the application would look like it had a data
/// problem. This is the one thing the platform is less able to do than a generated application, and
/// it is refused where somebody can still act on it.</para>
/// </summary>
public static class PlatformCapabilities
{
    /// <summary>What the platform cannot run in this definition. Empty for almost every
    /// application.</summary>
    public static IReadOnlyList<Diagnostic> Validate(JsonNode? definition)
    {
        if (definition is not JsonObject document) return [];
        if (document["custom"] is not JsonObject custom) return [];

        var functions = (custom["functions"] as JsonArray)?.Count ?? 0;
        var hooks = (custom["hooks"] as JsonArray)?.Count ?? 0;
        if (functions == 0 && hooks == 0) return [];

        var language = (string?)custom["language"] ?? "another language";

        return
        [
            new Diagnostic(DiagnosticCodes.CustomCode,
                $"this application carries custom {language} code. Cordango Platform runs an "
                + "application by interpreting its definition — there is no compiler in the runtime "
                + "and no sandbox — so a method you wrote cannot be executed there, and the figures "
                + "that depend on it would simply be blank. Build it with a target that compiles "
                + "your code into the application you own, or express the calculation with what the "
                + "language already has.",
                "$.custom"),
        ];
    }
}
