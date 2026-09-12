// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

/// <summary>
/// `custom` rides through the compiler on the machinery unknown root keys already use.
///
/// <para><b>These assert that nothing had to be written to make it work.</b> `CordSource.Split`
/// copies every root key it does not own into the app file and `Join` copies it back; `CordImport`
/// keeps what `CordApp` does not model in `Raw` and `CordLower` overlays it again. So the section
/// survives both trips with no entry in `Sections`, no `order` array and no special case anywhere —
/// and if somebody later adds one, one of these fails rather than the feature quietly acquiring a
/// second source of truth.</para>
/// </summary>
public class CustomSectionRoundTripTests
{
    private static JsonObject Section() => new()
    {
        ["language"] = "dotnet",
        ["hash"] = "sha256:" + new string('a', 64),
        ["functions"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "discount",
                ["returns"] = "number",
                ["params"] = new JsonArray
                {
                    new JsonObject { ["name"] = "total", ["kind"] = "number" },
                    new JsonObject { ["name"] = "tier", ["kind"] = "text" },
                },
                ["source"] = new JsonObject
                {
                    ["file"] = "Pricing.cs", ["type"] = "Pricing", ["method"] = "Discount",
                },
            },
        },
        ["hooks"] = new JsonArray
        {
            new JsonObject
            {
                ["entity"] = "expense_claim",
                ["event"] = "before_create",
                ["stage"] = "before_computed",
                ["source"] = new JsonObject
                {
                    ["file"] = "Rules.cs", ["type"] = "Rules", ["method"] = "Stamp",
                },
            },
        },
    };

    private static JsonObject WithCustom()
    {
        var path = Corpus.SemanticPaths().First(p => p.Contains("expenses", StringComparison.Ordinal));
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        var obj = doc.AsObject();
        obj["custom"] = Section();
        return obj;
    }

    [Fact]
    public void A_definition_carrying_custom_passes_the_schema()
    {
        Assert.Empty(Gate.StructuralErrors(WithCustom()));
    }

    [Fact]
    public void Custom_survives_the_trip_through_source_files()
    {
        var definition = WithCustom();

        var files = CordSource.Split(definition);
        var (rebuilt, problems) = CordSource.Join(files);

        Assert.Empty(problems);
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(rebuilt));
    }

    [Fact]
    public void Custom_lands_in_the_app_file_and_needs_no_order_entry()
    {
        var files = CordSource.Split(WithCustom());

        var appFile = files.Single(f => f.Path == CordSource.AppFile);
        Assert.NotNull(appFile.Document["custom"]);
        Assert.Null(appFile.Document["order"]?["custom"]);
        Assert.DoesNotContain(files, f => f.Path.Contains("custom", StringComparison.Ordinal));
    }

    [Fact]
    public void Custom_survives_the_semantic_round_trip()
    {
        var definition = WithCustom();

        var lowered = CordLower.Lower(CordImport.Import(definition));

        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(lowered));
    }

    private static List<string> Semantic(Action<JsonObject> mutate)
    {
        var doc = WithCustom();
        mutate(doc["custom"]!.AsObject());
        return Gate.SemanticErrors(doc);
    }

    [Fact]
    public void A_hook_naming_an_entity_the_app_does_not_have_is_refused()
    {
        var errors = Semantic(c => c["hooks"]![0]!["entity"] = "no_such_entity");

        Assert.Contains(errors, e => e.Contains("no_such_entity", StringComparison.Ordinal));
    }

    [Fact]
    public void A_function_declared_twice_is_refused()
    {
        var errors = Semantic(c => c["functions"]!.AsArray()
            .Add(c["functions"]![0]!.DeepClone()));

        Assert.Contains(errors, e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_parameters_with_one_name_are_refused()
    {
        var errors = Semantic(c => c["functions"]![0]!["params"]![1]!["name"] = "total");

        Assert.Contains(errors, e => e.Contains("two parameters", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stage_on_an_after_hook_is_refused()
    {
        var errors = Semantic(c =>
        {
            c["hooks"]![0]!["event"] = "after_create";
            c["hooks"]![0]!["stage"] = "before_computed";
        });

        Assert.Contains(errors, e => e.Contains("sets a stage", StringComparison.Ordinal));
    }

    [Fact]
    public void A_well_formed_section_raises_nothing()
    {
        Assert.Empty(Semantic(_ => { }));
    }

    [Fact]
    public void Editing_a_body_changes_the_application()
    {
        var before = WithCustom();
        var after = WithCustom();
        after["custom"]!["hash"] = "sha256:" + new string('b', 64);

        Assert.NotEqual(DefinitionHash.Of(before), DefinitionHash.Of(after));
    }
}
