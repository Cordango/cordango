// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Standalone.Tests;

public class BlockClassEmitTests
{
    private const string Marker = "bg-surface-2 pa-4 rounded-lg border";

    [Fact]
    public void An_authored_class_reaches_the_generated_application()
    {
        var definition = Definition("task-manager");
        Assert.True(Decorate(definition), "no page block to carry a class");

        var pages = Pages(definition, "task-manager");
        var carried = pages.Any(f => f.Content.Contains($"<div class=\"{Marker}\">", StringComparison.Ordinal));

        Assert.True(carried,
            "an authored `class` never reached the emitted templates, which is how a styling key "
            + "becomes a silent no-op in every generated application");
    }

    [Fact]
    public void A_block_without_a_class_gains_no_wrapper()
    {
        var pages = Pages(Definition("task-manager"), "task-manager");
        Assert.DoesNotContain(pages, f => f.Content.Contains("<div class=\"\">", StringComparison.Ordinal));
        Assert.DoesNotContain(pages, f => f.Content.Contains($"<div class=\"{Marker}\">", StringComparison.Ordinal));
    }

    private static bool Decorate(JsonObject definition)
    {
        foreach (var page in definition["pages"]?.AsArray() ?? [])
            if (page?["blocks"]?.AsArray() is { Count: > 0 } blocks && blocks[0] is JsonObject first)
            {
                first["class"] = Marker;
                return true;
            }
        return false;
    }

    private static JsonObject Definition(string key)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static IReadOnlyList<GeneratedFile> Pages(JsonObject definition, string key)
    {
        var outcome = CandidateValidator.Run(
            definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(outcome.Manifest is not null,
            $"{key} did not compile: {string.Join("; ", outcome.Errors)}");

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        var result = new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
        {
            ["allowIncomplete"] = true,
            ["seed"] = 42,
        }));

        return [.. result.Files.Where(f =>
            f.RelativePath.StartsWith("web/src/pages/", StringComparison.Ordinal))];
    }
}
