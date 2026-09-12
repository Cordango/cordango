// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.Node;

namespace Cordango.Node.Tests;

/// <summary>Compiling a corpus application and generating it, which every test here starts by
/// doing.</summary>
internal static class Build
{
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "schemas", "app-definition.schema.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root above " + AppContext.BaseDirectory + ".");
    }

    /// <summary>The reference applications, by key. The corpus this target is measured against is
    /// the same one the .NET target is, so neither can pass by being tested on less.</summary>
    public static IEnumerable<string> Corpus() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "tests", "corpus", "reference"), "*.appdef.json")
            .Select(path => Path.GetFileName(path).Replace(".appdef.json", "", StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal);

    public static TheoryData<string> CorpusData()
    {
        var data = new TheoryData<string>();
        foreach (var key in Corpus()) data.Add(key);
        return data;
    }

    /// <summary>One compiled application. The clock is fixed, because a generator whose output
    /// depends on the day it ran is not a generator this repository ships.</summary>
    public static CompiledAppArtifact Compile(string key)
    {
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json")))!.AsObject();

        var outcome = CandidateValidator.Run(
            definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new CompiledAppArtifact(
            outcome.Definition!.AsObject(),
            outcome.Manifest!,
            outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));
    }

    public static GenerateResult Generate(string key, bool allowIncomplete = true) =>
        new NodeVueGenerator().Generate(new GenerateRequest(
            Compile(key),
            new JsonObject { ["allowIncomplete"] = allowIncomplete, ["seed"] = 42 }));

    /// <summary>One generated file's content, or a failure naming what WAS generated — which is the
/// difference between "the test is wrong" and "the emitter is" taking ten seconds rather than
/// ten minutes.</summary>
    public static string Content(GenerateResult result, string path)
    {
        var found = result.Files.SingleOrDefault(f => f.RelativePath == path);
        Assert.True(found is not null,
            $"'{path}' was not generated. What was: {string.Join(", ", result.Files.Select(f => f.RelativePath))}");
        return found!.Content;
    }
}
