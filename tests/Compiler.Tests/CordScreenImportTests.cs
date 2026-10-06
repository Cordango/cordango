// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class CordScreenImportTests
{
    private static CordApp Authored()
    {
        var replay = CordJournal.Replay(new CordApp(Key: "budget", Name: "Budget", Version: "1.0.0"),
        [JsonNode.Parse("""
        [
          {"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"},
            {"key":"start","label":"Start","type":"date"}]}},
          {"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","subject":"hire",
            "sections":[
              {"key":"heads","kind":"metric","of":"hire","label":"Heads","value":{"op":"count"}},
              {"kind":"split","ratio":[2,1],"sections":[
                {"key":"trend","kind":"chart","of":"hire","label":"By team",
                 "value":{"op":"count"},"groupBy":"team","visual":"line"},
                {"key":"calendar","kind":"list","of":"hire","label":"Starts",
                 "view":"calendar","dateField":"start"}]}],
            "tabs":[
              {"key":"plan","label":"Plan","sections":[
                {"key":"lines","kind":"list","of":"hire","label":"Hiring lines",
                 "columns":["team","start"],"sort":[{"field":"start","direction":"asc"}]}]},
              {"key":"notes","label":"Notes","sections":[
                {"kind":"text","text":"Keep the plan current."}]}]}}
        ]
        """)]);
        Assert.True(replay.Complete, string.Join("\n", replay.Errors));
        return replay.Draft;
    }

    [Fact]
    public void Cord_lowered_screens_raise_back_to_editable_semantics()
    {
        var definition = CordLower.Lower(Authored());
        var imported = CordImport.Import(definition);

        Assert.NotNull(imported.Screens);
        Assert.False(imported.Raw?.ContainsKey("pages") ?? false);
        Assert.False(imported.Raw?.ContainsKey("views") ?? false);
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(CordLower.Lower(imported)));
        Assert.True(CordFiles.Materialize(imported).Complete);

        var written = CordDocument.Write(imported);
        Assert.True(written.Complete, string.Join("\n", written.Unwritable));
        var identity = new CordApp(imported.Key, imported.Name, imported.Version, imported.Description);
        var bootstrapped = CordJournal.Replay(identity, [written.Json["ops"]]);
        Assert.True(bootstrapped.Complete, string.Join("\n", bootstrapped.Errors));
        Assert.Equal(DefinitionHash.Of(definition),
            DefinitionHash.Of(CordLower.Lower(bootstrapped.Draft)));

        var changed = CordJournal.Replay(imported, [JsonNode.Parse("""
        [{"op":"upsert_screen_tab","screen":"hiring","tab":{"key":"notes","label":"Notes",
          "sections":[{"kind":"text","text":"Updated independently."}]}}]
        """)]);
        Assert.True(changed.Complete);
        Assert.Equal("Updated independently.", changed.Draft.Screens![0].Tabs![1].Sections![0].Text);
    }

    private static CordApp Fleet()
    {
        var replay = CordJournal.Replay(new CordApp(Key: "fleet", Name: "Fleet", Version: "1.0.0"),
        [JsonNode.Parse("""
        [
          {"op":"upsert_entity","entity":{"key":"car","label":"Car","fields":[
            {"key":"registration","label":"Registration","type":"text"},
            {"key":"make","label":"Make","type":"text"}]}},
          {"op":"upsert_screen","screen":{"key":"fleet","label":"Fleet","subject":"car","sections":[
            {"key":"cars","kind":"list","of":"car","label":"Cars","view":"cards",
             "columns":["registration","make"]},
            {"key":"log","kind":"list","of":"car","label":"Register","editable":false},
            {"key":"all","kind":"list","of":"car","label":"All cars"}]}}
        ]
        """)]);
        Assert.True(replay.Complete, string.Join("\n", replay.Errors));
        return replay.Draft;
    }

    [Fact]
    public void Cards_lower_to_a_table_drawn_as_cards_and_raise_back_to_cards()
    {
        var definition = CordLower.Lower(Fleet());
        var views = definition["views"]!.AsArray().OfType<JsonObject>().ToDictionary(v => (string)v["key"]!);

        Assert.Equal("table", (string?)views["fleet__cars"]["type"]);
        Assert.Equal("cards", (string?)views["fleet__cars"]["config"]!["display"]);

        var imported = CordImport.Import(definition);
        Assert.NotNull(imported.Screens);
        Assert.Equal("cards", imported.Screens![0].Sections![0].View);
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(CordLower.Lower(imported)));

        var written = CordDocument.Write(imported);
        Assert.True(written.Complete, string.Join("\n", written.Unwritable));
    }

    [Fact]
    public void A_table_is_editable_unless_the_section_says_otherwise_and_that_round_trips()
    {
        var definition = CordLower.Lower(Fleet());
        var views = definition["views"]!.AsArray().OfType<JsonObject>().ToDictionary(v => (string)v["key"]!);

        Assert.False((bool)views["fleet__log"]["config"]!["inlineEdit"]!);
        Assert.Null(views["fleet__all"]["config"]?["inlineEdit"]);

        var imported = CordImport.Import(definition);
        Assert.Equal(false, imported.Screens![0].Sections![1].Editable);
        Assert.Null(imported.Screens[0].Sections![2].Editable);
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(CordLower.Lower(imported)));
        Assert.True(CordDocument.Write(imported).Complete);
    }

    [Fact]
    public void An_unknown_page_shape_stays_raw_and_round_trips()
    {
        var definition = JsonNode.Parse("""
        {"key":"custom","name":"Custom","pages":[{"key":"p","label":"P","blocks":[
          {"kind":"orgchart","entity":"person"}]}],"views":[]}
        """)!;

        var imported = CordImport.Import(definition);

        Assert.Null(imported.Screens);
        Assert.True(imported.Raw!.ContainsKey("pages"));
        Assert.Equal(DefinitionHash.Of(definition), DefinitionHash.Of(CordLower.Lower(imported)));
        Assert.False(CordFiles.Materialize(imported).Complete);
    }
}
