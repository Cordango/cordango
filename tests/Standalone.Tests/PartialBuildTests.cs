// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class PartialBuildTests
{
    [Fact]
    public void An_unsupported_capability_no_longer_refuses_the_build()
    {
        var result = Build("ventures", allowIncomplete: true);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Files, f => f.RelativePath == "api/Program.cs");
        Assert.Contains(result.Files, f => f.RelativePath.StartsWith("api/Entities/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_build_still_refuses_unless_somebody_says_they_know()
    {
        var result = Build("ventures", allowIncomplete: false);

        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, d => d.Code == "CORD2102");
    }

    [Fact]
    public void The_block_becomes_a_card_that_says_so()
    {
        var files = Build("ventures", allowIncomplete: true).Files;

        Assert.Contains(files, f =>
            f.RelativePath.StartsWith("web/src/", StringComparison.Ordinal)
            && f.Content.Contains("<UnsupportedBlock kind=\"history\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_readme_separates_what_will_never_come_from_what_is_merely_late()
    {
        var readme = Build("ventures", allowIncomplete: true).Files
            .Single(f => f.RelativePath == "README.md").Content;

        Assert.Contains("## Partial build", readme, StringComparison.Ordinal);
        Assert.Contains("### Not supported by this target", readme, StringComparison.Ordinal);
        Assert.Contains("CORD2102", readme, StringComparison.Ordinal);
        Assert.Contains("will not appear in a later release", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrenderable_block_is_reported_once_and_with_the_right_reason()
    {
        var history = Build("ventures", allowIncomplete: true).Warnings
            .Where(d => d.Message.Contains("'history' block", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(history);
        Assert.All(history, d => Assert.Equal("CORD2102", d.Code));
        Assert.DoesNotContain(Build("ventures", allowIncomplete: true).Warnings,
            d => d.Code == "CORD2301" && d.Message.Contains("'history'", StringComparison.Ordinal));
    }

    private static GenerateResult Build(string key, bool allowIncomplete)
    {
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");

        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = allowIncomplete, ["seed"] = 42 }));
    }
}
