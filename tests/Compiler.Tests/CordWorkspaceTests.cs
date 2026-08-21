// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The workspace invariants — contract §10, the rules co-creation's safety rests on.
///
/// <para>Each of these was a real defect in the first draft of <see cref="CordWorkspace"/>, found by
/// review rather than by a failure, which is why they are pinned here as properties rather than left
/// as care taken at the call sites. The shape of every one is the same: the user accepts <b>one
/// aggregate they looked at</b>, so anything that could put unreviewed work inside that acceptance is
/// refused loudly instead of handled quietly.</para>
/// </summary>
public class CordWorkspaceTests
{
    private static CordApp Identity() => new(Key: "budget", Name: "Budget", Version: "1.0.0");

    /// <summary>One batch, as the journal stores it.</summary>
    private static JsonNode Batch(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void An_imported_definition_can_be_the_immutable_workspace_origin()
    {
        var authored = TwoScreens().Accepted;
        var imported = CordImport.Import(CordLower.Lower(authored));
        var workspace = CordWorkspace.Load(imported, [],
            new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));

        var change = workspace.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring forecast","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"}]}}]
        """));

        Assert.True(change.Applied);
        Assert.Equal("Hiring forecast", change.NextCandidate!.Screens![0].Label);
        Assert.Equal("Costs", change.NextCandidate.Screens[1].Label);
    }

    /// <summary>An accepted baseline with two screens, which is the smallest world where "editing one
    /// thing must not touch the other" means anything.</summary>
    private static CordWorkspace TwoScreens() => CordWorkspace.Load(Identity(),
    [
        Batch("""
        [
          {"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"}]}},
          {"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring","sections":[
            {"key":"lines","kind":"list","of":"hire","label":"Lines"}]}},
          {"op":"upsert_screen","screen":{"key":"costs","label":"Costs","sections":[
            {"key":"all","kind":"list","of":"hire","label":"All"}]}}]
        """),
    ]);

    // ---- §10.1 one candidate, one aggregate ------------------------------------------------------

    [Fact]
    public void An_operation_naming_another_aggregate_is_refused()
    {
        var open = TwoScreens().Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));

        var change = open.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"costs","label":"Costs rewritten","sections":[
          {"key":"all","kind":"metric","of":"hire","label":"Total","value":{"op":"count"}}]}}]
        """));

        Assert.False(change.Applied);
        Assert.Equal(CordErrorCode.OutsideScope, Assert.Single(change.Errors).Code);
        // A refused change leaves NOTHING behind — no half-applied draft to keep by accident.
        Assert.Null(change.NextCandidate);
        Assert.Null(change.Lowered);
    }

    [Fact]
    public void A_batch_is_refused_whole_when_one_operation_reaches_outside()
    {
        // The legal operation in position 0 does not survive the illegal one in position 1. Half of a
        // batch is half of an application nobody designed, and the author asked for both or neither.
        var open = TwoScreens().Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));

        var change = open.Apply(Batch("""
        [
          {"op":"upsert_screen_tab","screen":"hiring","tab":{"key":"plan","label":"Plan","sections":[
            {"key":"t","kind":"list","of":"hire","label":"Lines"}]}},
          {"op":"remove_screen","key":"costs"}]
        """));

        Assert.False(change.Applied);
        Assert.Equal(CordErrorCode.OutsideScope, Assert.Single(change.Errors).Code);
    }

    [Fact]
    public void A_screen_scope_reaches_its_own_tabs_but_not_another_screens()
    {
        // A tab is a child of its screen the way a field is a child of its entity, so the screen's
        // author can write it. Another screen's tab is another aggregate, and the key prefix is what
        // tells them apart.
        var open = TwoScreens().Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));

        var mine = open.Apply(Batch("""
        [{"op":"upsert_screen_tab","screen":"hiring","tab":{"key":"plan","label":"Plan",
          "sections":[{"key":"t","kind":"list","of":"hire","label":"Lines"}]}}]
        """));
        Assert.True(mine.Applied);

        var theirs = open.Apply(Batch("""
        [{"op":"upsert_screen_tab","screen":"costs","tab":{"key":"plan","label":"Plan",
          "sections":[{"key":"t","kind":"list","of":"hire","label":"Lines"}]}}]
        """));
        Assert.Equal(CordErrorCode.OutsideScope, Assert.Single(theirs.Errors).Code);
    }

    [Fact]
    public void Selecting_a_different_aggregate_while_a_candidate_is_open_is_refused()
    {
        // THE defect this test exists for: carrying a candidate across a scope change leaves the old
        // aggregate's operations inside the composed draft, so accepting the NEW aggregate would
        // silently write changes the user never reviewed into work they had approved.
        var open = TwoScreens().Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));
        var change = open.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"}]}}]
        """));
        var working = open.WithCandidate(change.NextCandidate!);

        Assert.Throws<InvalidOperationException>(() =>
            working.Select(new CordAggregateRef(CordAggregateKinds.Screen, "costs")));

        // Both explicit exits work, and re-opening the SAME aggregate is how a revision turn resumes.
        Assert.NotNull(working.Discard().Select(new CordAggregateRef(CordAggregateKinds.Screen, "costs")));
        Assert.NotNull(working.Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring")).Candidate);
    }

    [Fact]
    public void Applying_with_nothing_open_is_refused()
    {
        var change = TwoScreens().Apply(Batch("""
        [{"op":"remove_screen","key":"costs"}]
        """));

        Assert.False(change.Applied);
        Assert.Equal(CordErrorCode.OutsideScope, Assert.Single(change.Errors).Code);
    }

    // ---- §10.2 an unusable baseline is unusable for everything ------------------------------------

    [Fact]
    public void An_incomplete_baseline_refuses_every_use_and_not_just_authoring()
    {
        // The second batch is unreadable, so the replay stops and the baseline is a PREFIX of the
        // accepted work. Everything downstream would then describe an application missing whatever
        // came after the gap — silently, since the prefix is perfectly valid on its own.
        var broken = CordWorkspace.Load(Identity(),
        [
            Batch("""
            [{"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
              {"key":"team","label":"Team","type":"text"}]}}]
            """),
            Batch("""[{"op":"upsert_entity","entity":{"nonsense":true}}]"""),
        ]);

        Assert.True(broken.BaselineIncomplete);
        Assert.Throws<InvalidOperationException>(() => broken.Lowered());
        Assert.Throws<InvalidOperationException>(() => broken.ReviewHash());
        Assert.Throws<InvalidOperationException>(() => broken.Materialize());
        Assert.Throws<InvalidOperationException>(() => broken.Composed);
        Assert.Throws<InvalidOperationException>(() =>
            broken.Select(new CordAggregateRef(CordAggregateKinds.Domain)).Apply(Batch("""[]""")));
    }

    [Fact]
    public void An_incomplete_candidate_is_dropped_rather_than_half_kept()
    {
        // A candidate is cheap; half of one is dangerous — it would be presented as the current
        // proposal while describing work nobody saw whole.
        var workspace = CordWorkspace.Load(Identity(),
            [Batch("""
            [{"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
              {"key":"team","label":"Team","type":"text"}]}}]
            """)],
            new CordAggregateRef(CordAggregateKinds.Domain),
            [Batch("""[{"op":"upsert_field","entity":"hire","field":{"oops":1}}]""")]);

        Assert.True(workspace.CandidateDropped);
        Assert.Null(workspace.Candidate);
        // The baseline is intact, so the session can simply re-open the aggregate and try again.
        Assert.False(workspace.BaselineIncomplete);
        Assert.Single(workspace.Accepted.EntityList);
    }

    // ---- §10.3 no candidate, no review hash -------------------------------------------------------

    [Fact]
    public void A_review_hash_is_only_issued_for_a_candidate_somebody_can_see()
    {
        var baseline = TwoScreens();

        // With nothing open there is nothing anyone reviewed. Issuing a hash here would be a licence
        // to accept the baseline against itself — an accept arriving after a discard would match and
        // be honoured.
        Assert.Throws<InvalidOperationException>(() => baseline.ReviewHash());

        var open = baseline.Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));
        var change = open.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"}]}}]
        """));
        var working = open.WithCandidate(change.NextCandidate!);

        var hash = working.ReviewHash();
        Assert.Matches("^[0-9a-f]{64}$", hash);
        // It is the hash of what the preview shows — the composed document, not the candidate alone.
        Assert.Equal(hash, working.ReviewHash());

        // And it is gone the moment the candidate is.
        Assert.Throws<InvalidOperationException>(() => working.Discard().ReviewHash());
    }

    [Fact]
    public void Discard_restores_the_baseline_by_never_having_touched_it()
    {
        // The strongest form of "discard restores byte-for-byte" is the one obtained by not having
        // modified anything: the baseline is a separate value that Apply never writes to.
        var baseline = TwoScreens();
        var before = CordFiles.Materialize(baseline.Accepted);

        var open = baseline.Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));
        var change = open.Apply(Batch("""
        [{"op":"remove_screen","key":"hiring"}]
        """));
        var working = open.WithCandidate(change.NextCandidate!);

        var after = working.Discard().Materialize();
        Assert.Equal(before.Files.Select(f => f.Path), after.Files.Select(f => f.Path));
        Assert.Equal(before.Files.Select(f => f.ContentHash), after.Files.Select(f => f.ContentHash));
    }
}
