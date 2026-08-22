// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// A command's guard reaches the generated application.
///
/// <para>The evaluator is pinned by the shared fixtures and the emitter is pinned by its own tests.
/// This is the join: a definition that says "do not offboard somebody who already left" produces an
/// application whose command carries that condition. Without it, both halves could be perfect and
/// the generator could still forget to pass the guard along — which is exactly the state this was in
/// before, reported as CORD2304 and generated as nothing.</para>
/// </summary>
public class CommandGuardTests
{
    /// <summary>
    /// <c>people-hr</c> guards two commands with the same condition: an employee who is already
    /// <c>alumni</c> cannot be offboarded again.
    /// </summary>
    [Fact]
    public void A_guard_in_the_definition_becomes_a_guard_in_the_catalogue()
    {
        var catalogue = Generated("people-hr", "api/Commands/AppCommands.cs");

        Assert.Contains("using Cordango.Standalone.Conditions;", catalogue, StringComparison.Ordinal);
        Assert.Contains(
            "When: Condition.Leaf(\"employment_status\", \"neq\", \"alumni\")",
            catalogue,
            StringComparison.Ordinal);
    }

    /// <summary>A command with no guard says so, rather than carrying an always-true condition that
    /// somebody would then have to read to discover means nothing.</summary>
    [Fact]
    public void A_command_with_no_guard_carries_none()
    {
        Assert.Contains("When: null)", Generated("expenses", "api/Commands/AppCommands.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard is gone from the not-yet-emitted list.
    ///
    /// <para>A diagnostic that outlives the gap it described is worse than none: it teaches whoever
    /// reads the build output to ignore that code, and the next real one goes with it.</para>
    /// </summary>
    [Theory]
    [InlineData("people-hr")]
    [InlineData("ventures")]
    public void A_guarded_application_no_longer_reports_one_as_missing(string key)
    {
        var result = Build(key);

        Assert.DoesNotContain(result.Warnings, w => w.Code is "CORD2304" or "CORD2306");
    }

    private static string Generated(string key, string path) =>
        Build(key).Files.Single(f => f.RelativePath == path).Content;

    private static GenerateResult Build(string key)
    {
        var definition = JsonNode.Parse(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json")))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
                new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
