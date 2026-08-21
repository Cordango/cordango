// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

/// <summary>
/// Rebuilding a draft from the operations that made it.
///
/// <para>The pure half of session durability, testable with no database and no session. What a crashed
/// job actually needs is not "the last document" but "everything the author said, in order" — and the
/// difference is not academic: a re-imported document brings its screens back as raw overlay entries,
/// which Cord deliberately refuses to edit.</para>
/// </summary>
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

    /// <summary>Three concerns, three batches, one draft — the ordinary case, and the shape of every
    /// crash recovery below.</summary>
    [Fact]
    public void Replaying_the_journal_rebuilds_the_draft()
    {
        var replay = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Behaviour), Ops(Screens)]);

        Assert.True(replay.Complete);
        Assert.Equal(3, replay.Applied);
        Assert.Equal(["claim"], replay.Draft.EntityList.Select(e => e.Key));
        Assert.Single(replay.Draft.ProcessList);
        Assert.Equal(["claims"], replay.Draft.Screens!.Select(s => s.Key));
        // Identity survives — the replay starts from it rather than reconstructing it.
        Assert.Equal("claims", replay.Draft.Key);
        Assert.Equal("1.0.0", replay.Draft.Version);
    }

    /// <summary>Crash after the domain: the entities come back and behaviour can be added on top.</summary>
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

    /// <summary>Crash after domain AND behaviour: both survive, and screens still go on top.</summary>
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

    /// <summary>
    /// <b>Recovered screens remain editable through both durable representations.</b>
    ///
    /// <para>The journal stays the hosted recovery authority because it preserves accepted aggregate
    /// history. The AppDefinition path is the sharing/bootstrap route: shapes Cord lowered must raise
    /// back into the same independently editable screens.</para>
    /// </summary>
    [Fact]
    public void Replayed_and_reimported_cord_screens_stay_editable()
    {
        var replayed = CordJournal.Replay(Identity(), [Ops(Domain), Ops(Screens)]).Draft;

        // Semantic, not overlay — and therefore changeable.
        Assert.NotNull(replayed.Screens);
        Assert.Null(replayed.Raw?["pages"]);
        var edit = CordTransaction.Prepare(replayed, Ops("""
        [{"op":"upsert_screen","screen":{"key":"claims","label":"Every claim","subject":"claim",
          "sections":[{"kind":"list","of":"claim","label":"All"}]}}]
        """));
        Assert.True(edit.Ok, "a replayed screen must still be editable");
        Assert.Equal("Every claim", edit.Next.Screens![0].Label);

        // The sharing path: import the SAME app back from its lowered document.
        var imported = CordImport.Import(CordLower.Lower(replayed));
        Assert.Null(imported.Raw?["pages"]);
        Assert.NotNull(imported.Screens);
        // The screen remains semantic, so the same isolated edit is still safe.
        var blocked = CordTransaction.Prepare(imported, Ops("""
        [{"op":"upsert_screen","screen":{"key":"claims","label":"Every claim","subject":"claim",
          "sections":[{"kind":"list","of":"claim","label":"All"}]}}]
        """));
        Assert.True(blocked.Ok);
        Assert.Equal("Every claim", blocked.Next.Screens![0].Label);
    }

    /// <summary>
    /// A batch that will not apply STOPS the replay rather than being skipped.
    ///
    /// <para>Order is the entire meaning of a journal. Continuing past a gap would apply later
    /// operations to a draft that never existed — an <c>upsert_field</c> whose entity arrived in the
    /// batch that failed — and produce an application nobody authored, with nothing anywhere to say so.
    /// Stopping short is recoverable: the model carries on from a state that is genuinely real.</para>
    /// </summary>
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
        // Everything before the gap is there; nothing after it is.
        Assert.Single(replay.Draft.EntityList);
        Assert.Null(replay.Draft.Screens);
    }

    /// <summary>An empty journal is the ordinary first attempt, not an error: the draft is the identity
    /// it started from.</summary>
    [Fact]
    public void An_empty_journal_returns_the_identity_unchanged()
    {
        var replay = CordJournal.Replay(Identity(), []);

        Assert.True(replay.Complete);
        Assert.Equal(0, replay.Applied);
        Assert.Empty(replay.Draft.EntityList);
        Assert.Equal("claims", replay.Draft.Key);
    }

    /// <summary>Replaying the same journal twice gives the same draft. Upserts are keyed, so a retry
    /// that re-reads a row it already read cannot double an entity.</summary>
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
