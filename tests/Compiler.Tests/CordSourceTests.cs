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

        Assert.Equal(CordSource.AppFile, files[0].Path);

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

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void A_definition_survives_the_trip_through_source(string path)
    {
        var definition = Load(path);

        var files = CordSource.Split(definition);
        var (rebuilt, problems) = CordSource.Join(files);

        Assert.Empty(problems);

        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(rebuilt));
    }

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
        var app = CordImport.Import(Load(Corpus.SemanticPaths()
            .Single(p => p.EndsWith("budget-planner.appdef.json", StringComparison.Ordinal))));

        var files = CordSource.Write(app);

        Assert.Contains(files, f => f.Path == "views/entities/scenario/detail");
        Assert.Contains(files, f => f.Path == "views/entities/scenario/form");

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
