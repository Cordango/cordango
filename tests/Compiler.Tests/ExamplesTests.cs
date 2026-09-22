// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The worked examples `cordango example` serves.
///
/// <para>They are extracted from a shipped application rather than written here, so the question
/// this suite answers is not "are they good" but "are they still true": does each one still
/// validate against the block schema it claims to be an example of, and is each one still the kind
/// it is filed under. An example that has drifted is worse than no example, because it is read as
/// authoritative.</para>
/// </summary>
public class ExamplesTests
{
    [Fact]
    public void Every_authorable_block_kind_has_a_worked_example()
    {
        var missing = BlockKinds.All
            .Select(k => k.Canonical).Distinct(StringComparer.Ordinal)
            .Where(k => Examples.For(k) is null)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Kinds the model may author with no example to show it: " + string.Join(", ", missing)
            + ". Add an instance to the source application and re-run "
            + "catalog/export_examples.py.");
    }

    [Fact]
    public void The_two_hand_placed_kinds_have_one_too()
    {
        // Withheld from the model, but a person hand-editing a definition still places them, and
        // they are the two hardest to get right from the schema alone.
        Assert.NotNull(Examples.For("documents"));
        Assert.NotNull(Examples.For("relatedApps"));
    }

    [Fact]
    public void Nothing_is_filed_under_a_kind_it_is_not()
    {
        foreach (var kind in Examples.Kinds)
            Assert.Equal(kind, Examples.For(kind)!.Block["kind"]?.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(ExampleKinds))]
    public void Every_example_still_validates_against_its_own_block_schema(string kind)
    {
        var example = Examples.For(kind)!;

        // Validated as a BLOCK, through the one document shape the gate accepts: a definition
        // with a page holding it. Validating the fragment against `$defs/block_<kind>` alone
        // would miss the `$ref`s it resolves through, which is where the real constraints live.
        var doc = Wrap(example.Block);
        var errors = Gate.StructuralErrors(doc);

        Assert.True(errors.Count == 0,
            $"the example for '{kind}' no longer validates:\n  " + string.Join("\n  ", errors));
    }

    public static TheoryData<string> ExampleKinds()
    {
        var data = new TheoryData<string>();
        foreach (var kind in Examples.Kinds) data.Add(kind);
        return data;
    }

    /// <summary>The smallest legal definition that can hold one block, so a fragment can be put
    /// through the real structural gate rather than a hand-rolled approximation of it.</summary>
    private static JsonObject Wrap(JsonObject block) => new()
    {
        ["schemaVersion"] = "2.0",
        ["key"] = "example_host",
        ["name"] = "Example host",
        ["version"] = "1.0.0",
        ["entities"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "thing",
                ["label"] = "Thing",
                ["fields"] = new JsonArray
                {
                    new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text" },
                },
            },
        },
        ["pages"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "host",
                ["label"] = "Host",
                ["blocks"] = new JsonArray { block.DeepClone() },
            },
        },
    };
}
