// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordJournalTests
{
    private static CordApp Identity() => new(Key: "claims", Name: "Claims", Version: "1.0.0");

    private static JsonNode Ops(string json) => JsonNode.Parse(json)!;

    private const string Domain = """
    [{"op":"upsert_entity","entity":{"key":"claim","label":"Claim","fields":[
      {"key":"title","label":"Title","type":"text","required":true},
      {"key":"state","label":"State","type":"select","role":"status","options":[
        {"value":"draft","label":"Draft"},{"value":"approved","label":"Approved"}]}]}}]
    """;

    private const string Behaviour = """
    [{"op":"upsert_lifecycle","lifecycle":{
       "entity":"claim","stateField":"state","initialState":"draft",
       "states":[{"key":"draft","label":"Draft"},{"key":"approved","label":"Approved","terminal":true}],
       "transitions":[{"key":"approve","label":"Approve","from":["draft"],"to":"approved",
         "effects":[{"type":"notify","to":"{{record.title}}","message":"done"}]}]}}]
    """;

    private const string Screens = """
    [{"op":"upsert_screen","screen":{"key":"claims","label":"Claims","subject":"claim","sections":[
      {"kind":"list","of":"claim","label":"All claims"}]}}]
    """;

    [Fact]
    public void Replaying_the_journal_rebuilds_the_draft()
    {
        var replay = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Behaviour), Ops(Screens)]);

        Assert.True(replay.Complete);
        Assert.Equal(3, replay.Applied);
        Assert.Equal(["claim"], replay.Draft.EntityList.Select(e => e.Key));
        Assert.Single(replay.Draft.ProcessList);
        Assert.Equal(["claims"], replay.Draft.Screens!.Select(s => s.Key));
        Assert.Equal("claims", replay.Draft.Key);
        Assert.Equal("1.0.0", replay.Draft.Version);
    }

    [Fact]
    public void A_crash_after_the_domain_keeps_the_domain()
    {
        var replay = CordJournal.Replay(Identity(), [Ops(Domain)]);

        Assert.True(replay.Complete);
        Assert.Single(replay.Draft.EntityList);
        Assert.Null(replay.Draft.Processes);

        var next = CordTransaction.Prepare(replay.Draft, Ops(Behaviour));
        Assert.True(next.Ok);
        Assert.Single(next.Next.ProcessList);
        Assert.Single(next.Next.EntityList);
    }

    [Fact]
    public void A_crash_after_domain_and_behaviour_keeps_both_and_screens_can_follow()
    {
        var replay = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Behaviour)]);

        Assert.Equal(2, replay.Applied);
        Assert.Single(replay.Draft.EntityList);
        Assert.Single(replay.Draft.ProcessList);

        var next = CordTransaction.Prepare(replay.Draft, Ops(Screens));
        Assert.True(next.Ok);
        Assert.Equal(["claims"], next.Next.Screens!.Select(s => s.Key));
    }

    [Fact]
    public void Replayed_and_reimported_cord_screens_stay_editable()
    {
        var replayed = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Screens)]).Draft;

        Assert.NotNull(replayed.Screens);
        Assert.Null(replayed.Raw?["pages"]);
        var edit = CordTransaction.Prepare(replayed, Ops("""
        [{"op":"upsert_screen","screen":{"key":"claims","label":"Every claim","subject":"claim",
          "sections":[{"kind":"list","of":"claim","label":"All"}]}}]
        """));
        Assert.True(edit.Ok, "a replayed screen must still be editable");
        Assert.Equal("Every claim", edit.Next.Screens![0].Label);

        var imported = CordImport.Import(CordLower.Lower(replayed));
        Assert.Null(imported.Raw?["pages"]);
        Assert.NotNull(imported.Screens);
        var blocked = CordTransaction.Prepare(imported, Ops("""
        [{"op":"upsert_screen","screen":{"key":"claims","label":"Every claim","subject":"claim",
          "sections":[{"kind":"list","of":"claim","label":"All"}]}}]
        """));
        Assert.True(blocked.Ok);
        Assert.Equal("Every claim", blocked.Next.Screens![0].Label);
    }

    [Fact]
    public void A_batch_that_will_not_apply_stops_the_replay()
    {
        var replay = CordJournal.Replay(Identity(), [
            Ops(Domain),
            Ops("""[{"op":"upsert_field","entity":"ghost","field":{"key":"x","label":"X","type":"text"}}]"""),
            Ops(Screens),
        ]);

        Assert.False(replay.Complete);
        Assert.Equal(1, replay.Applied);
        Assert.Equal(CordErrorCode.UnknownEntity, Assert.Single(replay.Errors).Code);
        Assert.Single(replay.Draft.EntityList);
        Assert.Null(replay.Draft.Screens);
    }

    [Fact]
    public void An_empty_journal_returns_the_identity_unchanged()
    {
        var replay = CordJournal.Replay(Identity(), []);

        Assert.True(replay.Complete);
        Assert.Equal(0, replay.Applied);
        Assert.Empty(replay.Draft.EntityList);
        Assert.Equal("claims", replay.Draft.Key);
    }

    [Fact]
    public void Replaying_twice_is_the_same_as_replaying_once()
    {
        var once = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Behaviour), Ops(Screens)]).Draft;
        var twice = CordJournal.Replay(Identity(),
            [Ops(Domain), Ops(Behaviour), Ops(Screens), Ops(Domain), Ops(Behaviour), Ops(Screens)]).Draft;

        Assert.Equal(CordLower.Lower(once).ToJsonString(), CordLower.Lower(twice).ToJsonString());
    }

    [Fact]
    public void An_imported_definition_is_a_lossless_starting_point_for_later_operations()
    {
        var definition = CordLower.Lower(CordJournal.Replay(Identity(), [Ops(Domain), Ops(Screens)]).Draft);
        definition["presentation"] = new JsonObject { ["icon"] = "chart", ["color"] = "#5147e8" };

        var replay = CordJournal.Replay(new CordApp(),
        [
            CordJournal.Imported(definition),
            Ops("""[{"op":"upsert_field","entity":"claim","field":{"key":"note","label":"Note","type":"text"}}]"""),
        ]);

        Assert.True(replay.Complete);
        Assert.Equal(2, replay.Applied);
        Assert.Equal("#5147e8", (string?)replay.Draft.Raw?["presentation"]?["color"]);
        Assert.Contains(replay.Draft.EntityList.Single().FieldList, f => f.Key == "note");
    }

    [Fact]
    public void An_imported_definition_after_authored_history_is_a_gap_not_a_reset()
    {
        var replay = CordJournal.Replay(Identity(),
            [Ops(Domain), CordJournal.Imported(CordLower.Lower(Identity()))]);

        Assert.False(replay.Complete);
        Assert.Equal(1, replay.Applied);
        Assert.Single(replay.Draft.EntityList);
    }

    [Fact]
    public void A_human_advanced_replacement_is_lossless_and_later_semantic_edits_still_apply()
    {
        var initial = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Screens)]).Draft;
        var edited = CordLower.Lower(initial);
        edited["presentation"] = new JsonObject { ["icon"] = "beach", ["color"] = "#5147e8" };

        var replay = CordJournal.Replay(Identity(),
        [
            Ops(Domain),
            Ops(Screens),
            CordJournal.Replaced(edited),
            Ops("""[{"op":"upsert_field","entity":"claim","field":{"key":"note","label":"Note","type":"text"}}]"""),
        ]);

        Assert.True(replay.Complete);
        Assert.Equal(4, replay.Applied);
        Assert.Equal("#5147e8", (string?)replay.Draft.Raw?["presentation"]?["color"]);
        Assert.Contains(replay.Draft.EntityList.Single().FieldList, f => f.Key == "note");
    }
}
