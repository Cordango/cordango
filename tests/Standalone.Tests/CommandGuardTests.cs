// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class CommandGuardTests
{
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

    [Fact]
    public void A_command_with_no_guard_carries_none()
    {
        Assert.Contains("When: null)", Generated("expenses", "api/Commands/AppCommands.cs"), StringComparison.Ordinal);
    }

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
