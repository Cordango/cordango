// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <see cref="CordSource"/> is the writer that has to be TOTAL, and these are the two claims that
/// make it worth having.
///
/// <para>The measurement that motivated it, taken 2026-08-13: <c>CordDocument.Write</c> reported
/// unwritable pointers for <b>all 15</b> reference apps — the operation vocabulary cannot express a
/// raw fragment, and the entire visual layer is one. Nothing could be written to files.</para>
/// </summary>
public class CordSourceTests(ITestOutputHelper output)
{
    private static JsonNode Load(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void Every_aggregate_in_the_corpus_gets_a_file(string path)
    {
        var app = CordImport.Import(Load(path));
        var files = CordSource.Write(app);

        // One identity file, and one per aggregate. The layout is the contract — a host appends the
        // syntax extension and nothing else.
        Assert.Equal(CordSource.AppFile, files[0].Path);

        // Named rather than counted: "33 != 30" sends you hunting, "these three paths collide" does not.
        var duplicates = files.GroupBy(f => f.Path, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "two aggregates claim one file: " + string.Join(", ", duplicates));

        var lowered = (JsonObject)CordLower.Lower(app);
        foreach (var entity in lowered["entities"] as JsonArray ?? [])
            Assert.Contains(files, f => f.Path == $"entities/{(string?)entity!["key"]}");
        foreach (var page in lowered["pages"] as JsonArray ?? [])
            Assert.Contains(files, f => f.Path == $"views/screens/{(string?)page!["key"]}");
        foreach (var view in lowered["views"] as JsonArray ?? [])
            Assert.Contains(files, f => f.Path == $"views/collections/{(string?)view!["key"]}");

        output.WriteLine($"{Path.GetFileName(path),-30} {files.Count,3} files");
    }

    /// <summary>
    /// THE test. Split a definition into files, join them back, and require the same document.
    ///
    /// <para>Everything else here is a detail of the layout; this is whether the format can carry an
    /// application at all. It is the same assertion
    /// <c>examples/semantic/budgetPlanner/verify.py</c> makes against the hand-authored specimen, so
    /// the C# writer and the specimen are held to one standard rather than two.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void A_definition_survives_the_trip_through_source(string path)
    {
        var definition = Load(path);

        var files = CordSource.Split(definition);
        var (rebuilt, problems) = CordSource.Join(files);

        Assert.Empty(problems);

        // The App Definition hash, not a property-by-property comparison: it is the identity the gate
        // and `finish` already use, and it covers ARRAY ORDER — which a set of files does not have and
        // which the app file's `order` block exists to carry.
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(rebuilt));
    }

    /// <summary>The claim that motivated the whole component: every reference app can be written to
    /// files. <c>CordDocument.Write</c> managed none of them.</summary>
    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void Every_app_round_trips_through_a_CordApp_as_well(string path)
    {
        var app = CordImport.Import(Load(path));

        var (rebuilt, problems) = CordSource.Read(CordSource.Write(app));

        Assert.Empty(problems);
        Assert.NotNull(rebuilt);
        Assert.Equal(
            DefinitionHash.Of(CordLower.Lower(app)),
            DefinitionHash.Of(CordLower.Lower(rebuilt!)));
    }

    [Fact]
    public void A_record_surface_leaves_the_entity_it_describes()
    {
        // The complaint that started this: somebody went looking for the Budget Planner's scenario
        // detail view under views/, did not find it, and concluded it was missing. It was filed
        // inside the data model, where it was half of a 632-line entity file.
        var app = CordImport.Import(Load(Corpus.SemanticPaths()
            .Single(p => p.EndsWith("budget-planner.appdef.json", StringComparison.Ordinal))));

        var files = CordSource.Write(app);

        Assert.Contains(files, f => f.Path == "views/entities/scenario/detail");
        Assert.Contains(files, f => f.Path == "views/entities/scenario/form");

        // ...and its eight tabs are eight files, which is the granularity Cord already models.
        foreach (var tab in new[] { "projection", "assumptions", "pricing", "hiring",
                                    "costs", "funding", "story", "activity" })
            Assert.Contains(files, f => f.Path == $"views/entities/scenario/tabs/{tab}");

        var entity = files.Single(f => f.Path == "entities/scenario").Document;
        foreach (var surface in CordSource.Surfaces)
            Assert.False(entity.ContainsKey(surface), $"'{surface}' is presentation and must not be here");
    }

    [Fact]
    public void The_app_file_records_the_order_a_directory_cannot()
    {
        // Array order is meaningful — entity order drives navigation, page order drives the shell —
        // and DefinitionHash covers it. A set of files has none and a Git tree has none either, so
        // without this a round trip rebuilds every value and opens the app on the wrong page.
        var app = CordImport.Import(Load(Corpus.SemanticPaths()
            .Single(p => p.EndsWith("budget-planner.appdef.json", StringComparison.Ordinal))));

        var order = CordSource.Write(app)[0].Document["order"] as JsonObject;
        Assert.NotNull(order);

        var lowered = (JsonObject)CordLower.Lower(app);
        Assert.Equal(
            (lowered["entities"] as JsonArray ?? []).Select(e => (string?)e!["key"]),
            (order!["entities"] as JsonArray ?? []).Select(k => (string?)k));
        Assert.Equal(
            (lowered["pages"] as JsonArray ?? []).Select(p => (string?)p!["key"]),
            (order["pages"] as JsonArray ?? []).Select(k => (string?)k));
    }
}
