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

public class CordFileTests(ITestOutputHelper output)
{
    private static JsonNode Baseline(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    private static CordApp TwoScreenApp()
    {
        var identity = new CordApp(Key: "budget", Name: "Budget", Version: "1.0.0");
        var replay = CordJournal.Replay(identity, [JsonNode.Parse("""
        [
          {"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"}]}},
          {"op":"upsert_entity","entity":{"key":"scenario","label":"Scenario","fields":[
            {"key":"name","label":"Name","type":"text"}]}},
          {"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring","sections":[
            {"key":"lines","kind":"list","of":"hire","label":"Lines"}]}},
          {"op":"upsert_screen","screen":{"key":"costs","label":"Costs","sections":[
            {"key":"all","kind":"list","of":"hire","label":"All"}]}}]
        """)]);
        Assert.True(replay.Complete);
        return replay.Draft;
    }

    [Fact]
    public void One_aggregate_is_one_file()
    {
        var files = CordFiles.Materialize(TwoScreenApp());

        Assert.True(files.Complete);
        Assert.Equal(
            ["app.cord", "entities/hire.cord", "entities/scenario.cord",
             "views/hiring.cord", "views/costs.cord"],
            files.Files.Select(f => f.Path));

        foreach (var file in files.Files.Skip(1))
        {
            var ops = (JsonArray)JsonNode.Parse(file.Content)!["ops"]!;
            Assert.All(ops, op => Assert.Equal(file.Path, CordFiles.PathOf((JsonObject)op!)));
        }
    }

    [Fact]
    public void The_bytes_are_canonical()
    {
        var file = CordFiles.Materialize(TwoScreenApp()).Files.Single(f => f.Path == "views/hiring.cord");

        Assert.DoesNotContain('\r', file.Content);
        Assert.EndsWith("\n", file.Content, StringComparison.Ordinal);
        Assert.Contains("\n  ", file.Content, StringComparison.Ordinal);
        Assert.Equal(DefinitionHash.OfText(file.Content), file.ContentHash);

        Assert.Equal(file.Content,
            CordFiles.Materialize(TwoScreenApp()).Files.Single(f => f.Path == "views/hiring.cord").Content);
    }

    [Fact]
    public void Accepting_one_screen_changes_exactly_one_file()
    {
        var before = CordFiles.Materialize(TwoScreenApp());

        var accepted = CordJournal.Replay(TwoScreenApp(), [JsonNode.Parse("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"},
          {"key":"heads","kind":"metric","of":"hire","label":"Heads","value":{"op":"count"}}]}}]
        """)]);
        Assert.True(accepted.Complete);

        var after = CordFiles.Materialize(accepted.Draft);

        Assert.Equal(before.Files.Select(f => f.Path), after.Files.Select(f => f.Path));

        var changed = before.Files
            .Zip(after.Files, (b, a) => (a.Path, Same: b.ContentHash == a.ContentHash))
            .Where(x => !x.Same)
            .Select(x => x.Path)
            .ToList();

        Assert.Equal(["views/hiring.cord"], changed);
    }

    [Fact]
    public void Screen_files_keep_section_identity_and_tabs()
    {
        var app = CordJournal.Replay(TwoScreenApp(), [JsonNode.Parse("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring","sections":[
          {"key":"headline","kind":"metric","of":"hire","label":"Heads","value":{"op":"count"}}],
          "tabs":[
            {"key":"plan","label":"Plan","sections":[
              {"key":"lines","kind":"list","of":"hire","label":"Lines"}]},
            {"key":"cost","label":"Cost","sections":[
              {"key":"by_team","kind":"chart","of":"hire","label":"By team",
               "value":{"op":"count"},"groupBy":"team"}]}]}}]
        """)]);
        Assert.True(app.Complete, string.Join("\n", app.Errors));

        var files = CordFiles.Materialize(app.Draft);
        Assert.True(files.Complete);
        var screen = files.Files.Single(f => f.Path == "views/hiring.cord").Content;

        Assert.Contains("\"key\": \"headline\"", screen, StringComparison.Ordinal);
        Assert.Contains("\"tabs\"", screen, StringComparison.Ordinal);
        Assert.Contains("\"key\": \"plan\"", screen, StringComparison.Ordinal);
        Assert.Contains("\"key\": \"by_team\"", screen, StringComparison.Ordinal);

        var (doc, problems) = CordFiles.Join(files.Files);
        Assert.Empty(problems);
        var replay = CordTransaction.Prepare(
            new CordApp(Key: (string?)doc["key"], Name: (string?)doc["name"], Version: (string?)doc["version"]),
            doc["ops"]);
        Assert.Empty(replay.Errors);
        Assert.Equal(DefinitionHash.Of(CordLower.Lower(app.Draft)),
            DefinitionHash.Of(CordLower.Lower(replay.Next!)));
    }

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void An_application_survives_the_trip_through_files(string path)
    {
        var app = CordImport.Import(Baseline(path));
        var files = CordFiles.Materialize(app);

        if (!files.Complete)
        {
            output.WriteLine($"{Path.GetFileName(path)}: {files.Unwritable.Count} unwritable");
            return;
        }

        var (doc, problems) = CordFiles.Join(files.Files);
        Assert.Empty(problems);

        var blank = new CordApp(
            Key: (string?)doc["key"], Name: (string?)doc["name"], Version: (string?)doc["version"],
            Description: (string?)doc["description"]);
        var prepared = CordTransaction.Prepare(blank, doc["ops"]);
        Assert.Empty(prepared.Errors);

        Assert.Equal(DefinitionHash.Of(CordLower.Lower(app)),
            DefinitionHash.Of(CordLower.Lower(prepared.Next!)));
    }

    [Fact]
    public void The_file_order_is_recorded_so_a_tree_can_rebuild_it()
    {
        var app = TwoScreenApp();
        var files = CordFiles.Materialize(app).Files;

        var shuffled = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var (doc, problems) = CordFiles.Join(shuffled);
        Assert.Empty(problems);

        var prepared = CordTransaction.Prepare(
            new CordApp(Key: (string?)doc["key"], Name: (string?)doc["name"], Version: (string?)doc["version"]),
            doc["ops"]);
        Assert.Equal(DefinitionHash.Of(CordLower.Lower(app)),
            DefinitionHash.Of(CordLower.Lower(prepared.Next!)));
    }

    [Fact]
    public void An_operation_in_the_wrong_file_is_refused_rather_than_replayed()
    {
        var files = CordFiles.Materialize(TwoScreenApp()).Files.ToList();
        var screen = files.Single(f => f.Path == "views/hiring.cord");

        var tampered = (JsonObject)JsonNode.Parse(screen.Content)!;
        ((JsonArray)tampered["ops"]!).Add(JsonNode.Parse("""{"op":"remove","entity":"scenario"}"""));
        files[files.IndexOf(screen)] = CordFiles.FromContent(screen.Path, tampered.ToJsonString());

        var (doc, problems) = CordFiles.Join(files);

        var problem = Assert.Single(problems);
        Assert.Contains("belongs in entities/scenario.cord", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("\"remove\"", doc["ops"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_that_disagrees_with_its_hash_is_refused()
    {
        var files = CordFiles.Materialize(TwoScreenApp()).Files.ToList();
        var entity = files.Single(f => f.Path == "entities/hire.cord");
        files[files.IndexOf(entity)] = entity with { Content = entity.Content.Replace("Team", "Squad") };

        var (_, problems) = CordFiles.Join(files);

        Assert.Contains(problems, p => p.Contains("does not match its recorded hash", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tree_missing_its_identity_file_says_so()
    {
        var files = CordFiles.Materialize(TwoScreenApp()).Files.Where(f => f.Path != CordFiles.AppFile);

        var (_, problems) = CordFiles.Join(files);

        Assert.Contains(problems, p => p.StartsWith("app.cord: missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_the_identity_does_not_list_is_appended_rather_than_dropped()
    {
        var files = CordFiles.Materialize(TwoScreenApp()).Files.ToList();
        files.Add(CordFiles.FromContent("entities/team.cord", """
        {"ops":[{"op":"upsert_entity","entity":{"key":"team","label":"Team","fields":[
          {"key":"name","label":"Name","type":"text"}]}}]}
        """));

        var (doc, problems) = CordFiles.Join(files);

        Assert.Empty(problems);
        Assert.Contains("\"team\"", doc["ops"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_listed_file_that_is_absent_is_named()
    {
        var files = CordFiles.Materialize(TwoScreenApp()).Files
            .Where(f => f.Path != "entities/scenario.cord");

        var (_, problems) = CordFiles.Join(files);

        Assert.Contains(problems, p =>
            p.StartsWith("entities/scenario.cord: listed", StringComparison.Ordinal));
    }
}
