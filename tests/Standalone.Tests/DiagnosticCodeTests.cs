// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class DiagnosticCodeTests
{
    [Fact]
    public void Every_code_the_corpus_raises_is_one_somebody_registered()
    {
        var known = DiagnosticCodes.All.Concat(NotYetCodes.All).ToHashSet(StringComparer.Ordinal);

        var raised = Corpus()
            .SelectMany(key => Build(key).Warnings.Concat(Build(key).Errors))
            .Select(d => d.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(raised);
        Assert.All(raised, code => Assert.True(known.Contains(code),
            $"{code} is raised by a build and is in neither DiagnosticCodes.All nor NotYetCodes.All. "
            + "Register it, so the next target cannot pick the same number for something else."));
    }

    [Fact]
    public void No_build_raises_a_retired_code()
    {
        var raised = Corpus().SelectMany(key => Build(key).Warnings).Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(raised.Intersect(NotYetCodes.Retired, StringComparer.Ordinal));
    }

    private static IEnumerable<string> Corpus() =>
        System.IO.Directory.EnumerateFiles(
                Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference"), "*.appdef.json")
            .Select(path => Path.GetFileName(path).Replace(".appdef.json", "", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal);

    private static GenerateResult Build(string key)
    {
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json")))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
                new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
