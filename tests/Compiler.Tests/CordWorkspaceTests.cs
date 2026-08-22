// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordWorkspaceTests
{
    private static CordApp Identity() => new(Key: "budget", Name: "Budget", Version: "1.0.0");

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
        Assert.Null(change.NextCandidate);
        Assert.Null(change.Lowered);
    }

    [Fact]
    public void A_batch_is_refused_whole_when_one_operation_reaches_outside()
    {
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
        var open = TwoScreens().Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));
        var change = open.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"}]}}]
        """));
        var working = open.WithCandidate(change.NextCandidate!);

        Assert.Throws<InvalidOperationException>(() =>
            working.Select(new CordAggregateRef(CordAggregateKinds.Screen, "costs")));

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

    [Fact]
    public void An_incomplete_baseline_refuses_every_use_and_not_just_authoring()
    {
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
        var workspace = CordWorkspace.Load(Identity(),
            [Batch("""
            [{"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
              {"key":"team","label":"Team","type":"text"}]}}]
            """)],
            new CordAggregateRef(CordAggregateKinds.Domain),
            [Batch("""[{"op":"upsert_field","entity":"hire","field":{"oops":1}}]""")]);

        Assert.True(workspace.CandidateDropped);
        Assert.Null(workspace.Candidate);
        Assert.False(workspace.BaselineIncomplete);
        Assert.Single(workspace.Accepted.EntityList);
    }

    [Fact]
    public void A_review_hash_is_only_issued_for_a_candidate_somebody_can_see()
    {
        var baseline = TwoScreens();

        Assert.Throws<InvalidOperationException>(() => baseline.ReviewHash());

        var open = baseline.Select(new CordAggregateRef(CordAggregateKinds.Screen, "hiring"));
        var change = open.Apply(Batch("""
        [{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","sections":[
          {"key":"lines","kind":"list","of":"hire","label":"Every line"}]}}]
        """));
        var working = open.WithCandidate(change.NextCandidate!);

        var hash = working.ReviewHash();
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, working.ReviewHash());

        Assert.Throws<InvalidOperationException>(() => working.Discard().ReviewHash());
    }

    [Fact]
    public void Discard_restores_the_baseline_by_never_having_touched_it()
    {
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
