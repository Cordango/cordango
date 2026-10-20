// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Integrations;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class TransformFixtureTests
{
    private static IEnumerable<(string File, JsonObject Case)> All(string list)
    {
        var dir = Path.Combine(Corpus.RepoRoot(), "tests", "fixtures", "transforms");
        foreach (var path in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
            foreach (var c in (JsonNode.Parse(File.ReadAllText(path))![list] as JsonArray ?? []).OfType<JsonObject>())
                yield return (Path.GetFileName(path), c);
    }

    private static ContractField Target(JsonObject c)
    {
        var target = (JsonObject)c["target"]!;
        var options = (target["options"] as JsonArray ?? []).Select(o => (string)o!).ToList();
        return new ContractField("value", "Value", (string)target["type"]!, false, false, options);
    }

    private static List<string> Check(JsonObject c)
    {
        var errors = new List<string>();
        if (ApiPath.TryParse((string?)c["from"], out var perr) is null) errors.Add(perr!);
        Transforms.Check(c["transform"], Target(c), "fixture", errors);
        return errors;
    }

    [Fact]
    public void There_are_fixtures_to_hold_the_implementations_to()
    {
        Assert.True(All("cases").Count() >= 30);
        Assert.True(All("refused").Count() >= 8);
    }

    [Fact]
    public void Every_case_type_checks_clean()
    {
        foreach (var (file, c) in All("cases"))
            Assert.True(Check(c).Count == 0, $"{file}: {c["why"]}: {string.Join("; ", Check(c))}");
    }

    [Fact]
    public void Every_refused_mapping_is_refused_for_its_reason()
    {
        foreach (var (file, c) in All("refused"))
        {
            var errors = Check(c);
            var expected = (string)c["error"]!;
            Assert.True(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)),
                $"{file}: {c["why"]}: expected '{expected}', got: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_step_names_a_registered_transform()
    {
        foreach (var (file, c) in All("cases"))
            foreach (var step in Transforms.Steps(c["transform"]))
                Assert.True(Transforms.NameOf(step) is not null, $"{file}: {c["why"]}");
    }
}
