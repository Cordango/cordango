// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>DesignAssembler: per-screen slice outputs stitch into ONE design document that the
/// unchanged DesignMerge + Gate accept; refine can swap a single slice without touching the rest.</summary>
public class DesignAssemblerTests
{
    private static JsonObject Domain() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "support", "name": "Support", "version": "1.0.0",
      "entities": [
        { "key": "ticket", "label": "Ticket", "displayField": "subject", "fields": [
          { "key": "subject", "label": "Subject", "type": "text", "required": true },
          { "key": "stage", "label": "Stage", "type": "select", "role": "status",
            "options": [ { "value": "open", "label": "Open" }, { "value": "closed", "label": "Closed" } ] } ] },
        { "key": "customer", "label": "Customer", "displayField": "name", "fields": [
          { "key": "name", "label": "Name", "type": "text" } ] }
      ]
    }
    """)!;

    private static DesignPlan Plan() => DesignPlan.Parse(JsonNode.Parse("""
    {
      "theme": { "primaryColor": "#0e7490" },
      "presentation": { "icon": "ticket", "color": "#0e7490", "tagline": "Tickets" },
      "pages": [
        { "key": "home", "label": "Overview", "role": "home", "views": [] },
        { "key": "tickets", "label": "Tickets", "entity": "ticket", "role": "workspace", "views": [
          { "key": "tickets_table", "label": "All", "type": "table", "entity": "ticket" },
          { "key": "tickets_board", "label": "Board", "type": "kanban", "entity": "ticket" } ] },
        { "key": "customers", "label": "Customers", "entity": "customer", "role": "workspace", "views": [
          { "key": "customers_table", "label": "All", "type": "table", "entity": "customer" } ] }
      ],
      "details": [ { "entity": "ticket", "form": true } ]
    }
    """))!;

    private static Dictionary<string, JsonNode> Landed() => new()
    {
        ["page:home"] = JsonNode.Parse("""
        { "page": { "key": "home", "label": "Overview", "blocks": [
            { "kind": "card", "label": "Open tickets", "blocks": [
              { "kind": "stat", "source": { "entity": "ticket", "aggregate": { "op": "count" } } } ] } ] },
          "views": [] }
        """)!,
        ["page:tickets"] = JsonNode.Parse("""
        { "page": { "key": "tickets", "label": "Tickets", "entity": "ticket", "blocks": [
            { "kind": "tabs", "tabs": [
              { "label": "All", "blocks": [ { "kind": "view", "view": "tickets_table" } ] },
              { "label": "Board", "blocks": [ { "kind": "view", "view": "tickets_board" } ] } ] } ] },
          "views": [
            { "key": "tickets_table", "label": "All", "type": "table", "entity": "ticket",
              "config": { "columns": ["subject", "stage"] } },
            { "key": "tickets_board", "label": "Board", "type": "kanban", "entity": "ticket",
              "config": { "groupByField": "stage", "cardFields": ["subject"] } } ] }
        """)!,
        ["page:customers"] = JsonNode.Parse("""
        { "page": { "key": "customers", "label": "Customers", "entity": "customer", "blocks": [
            { "kind": "view", "view": "customers_table" } ] },
          "views": [
            { "key": "customers_table", "label": "All", "type": "table", "entity": "customer",
              "config": { "columns": ["name"] } } ] }
        """)!,
        ["detail:ticket"] = JsonNode.Parse("""
        { "detail": { "entity": "ticket", "blocks": [
            { "kind": "hub", "actions": ["edit", "delete"] },
            { "kind": "fields", "fields": ["subject", "stage"] } ] },
          "form": { "entity": "ticket", "blocks": [
            { "kind": "section", "label": "What happened?", "blocks": [
              { "kind": "fields", "fields": ["subject"] } ] } ] } }
        """)!,
    };

    [Fact]
    public void Assembled_slices_merge_and_pass_the_gate()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out var issues);
        Assert.Empty(issues);

        var merged = DesignMerge.Apply(Domain(), design, out var mergeIssues).AsObject();
        Assert.Empty(mergeIssues);
        Assert.Empty(Gate.Validate(merged));

        // Plan order IS the page order (which is the sidebar order — nav is never authored).
        Assert.Equal(["home", "tickets", "customers"],
            merged["pages"]!.AsArray().Select(p => p!["key"]!.GetValue<string>()));
        Assert.Null(merged["navigation"]);
        Assert.Equal("#0e7490", merged["theme"]!["primaryColor"]!.GetValue<string>());
        Assert.Equal("fields", merged["entities"]![0]!["detail"]!["blocks"]![1]!["kind"]!.GetValue<string>());
        Assert.NotNull(merged["entities"]![0]!["form"]);
    }

    [Fact]
    public void A_missing_slice_leaves_a_hole_not_a_failure()
    {
        var landed = Landed();
        landed.Remove("page:customers");
        var design = DesignAssembler.Assemble(Plan(), landed, out _);

        var merged = DesignMerge.Apply(Domain(), design, out _).AsObject();
        Assert.Empty(Gate.Validate(merged));                 // still a valid app
        Assert.Equal(["home", "tickets"],
            merged["pages"]!.AsArray().Select(p => p!["key"]!.GetValue<string>()));
    }

    [Fact]
    public void Views_outside_the_slices_assignment_are_dropped_with_an_issue()
    {
        var landed = Landed();
        landed["page:customers"] = JsonNode.Parse("""
        { "page": { "key": "customers", "label": "Customers", "blocks": [
            { "kind": "view", "view": "customers_table" } ] },
          "views": [
            { "key": "customers_table", "label": "All", "type": "table", "entity": "customer" },
            { "key": "rogue_view", "label": "Rogue", "type": "table", "entity": "ticket" } ] }
        """)!;
        var design = DesignAssembler.Assemble(Plan(), landed, out var issues);

        Assert.Contains(issues, i => i.Contains("rogue_view"));
        Assert.DoesNotContain(design["views"]!.AsArray(),
            v => v!["key"]!.GetValue<string>() == "rogue_view");
    }

    [Fact]
    public void StripDesign_returns_the_pure_domain()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var merged = DesignMerge.Apply(Domain(), design, out _).AsObject();

        var stripped = DesignAssembler.StripDesign(merged);
        Assert.Equal(Domain().ToJsonString(), stripped.ToJsonString());
    }

    // ---- the refine path: derive the roster, swap one slice --------------------------------------

    [Fact]
    public void FromDefinition_derives_the_slice_roster_from_a_merged_app()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var merged = DesignMerge.Apply(Domain(), design, out _).AsObject();

        var plan = DesignPlan.FromDefinition(merged)!;
        Assert.Equal(["home", "tickets", "customers"], plan.Pages.Select(p => p.Key));
        Assert.Equal("home", plan.Pages[0].Role);
        Assert.Equal(["tickets_table", "tickets_board"], plan.Pages[1].Views.Select(v => v.Key));
        Assert.Single(plan.Details);
        Assert.Equal("ticket", plan.Details[0].Entity);
        Assert.True(plan.Details[0].Form);
    }

    [Fact]
    public void FromDefinition_attaches_orphan_views_to_the_page_about_their_entity()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var merged = DesignMerge.Apply(Domain(), design, out _).AsObject();
        merged["views"]!.AsArray().Add(JsonNode.Parse("""
        { "key": "orphan", "label": "Orphan", "type": "table", "entity": "customer" }
        """));

        var plan = DesignPlan.FromDefinition(merged)!;
        Assert.Contains("orphan", plan.Pages.Single(p => p.Key == "customers").Views.Select(v => v.Key));
    }

    [Fact]
    public void ReplaceSlice_swaps_one_page_and_leaves_everything_else_byte_identical()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var current = DesignMerge.Apply(Domain(), design, out _).AsObject();
        var plan = DesignPlan.FromDefinition(current)!;

        var updated = current.DeepClone().AsObject();
        DesignAssembler.ReplaceSlice(updated, plan, "page:customers", JsonNode.Parse("""
        { "page": { "key": "customers", "label": "Clients", "entity": "customer", "blocks": [
            { "kind": "view", "view": "customers_table" } ] },
          "views": [
            { "key": "customers_table", "label": "Clients", "type": "table", "entity": "customer",
              "config": { "columns": ["name"] } } ] }
        """)!);

        Assert.Empty(Gate.Validate(updated));
        Assert.Equal("Clients", updated["pages"]!.AsArray()
            .Single(p => p!["key"]!.GetValue<string>() == "customers")!["label"]!.GetValue<string>());
        // Untouched slices are byte-identical.
        Assert.Equal(current["pages"]![0]!.ToJsonString(), updated["pages"]![0]!.ToJsonString());
        Assert.Equal(current["pages"]![1]!.ToJsonString(), updated["pages"]![1]!.ToJsonString());
        Assert.Equal(current["entities"]!.ToJsonString(), updated["entities"]!.ToJsonString());
        Assert.Equal(
            current["views"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "tickets_table")!.ToJsonString(),
            updated["views"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "tickets_table")!.ToJsonString());
    }

    [Fact]
    public void ReplaceSlice_theme_touches_theme_and_presentation_only()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var current = DesignMerge.Apply(Domain(), design, out _).AsObject();
        var plan = DesignPlan.FromDefinition(current)!;

        var updated = current.DeepClone().AsObject();
        DesignAssembler.ReplaceSlice(updated, plan, PlanGate.ThemeSliceId, JsonNode.Parse("""
        { "theme": { "primaryColor": "#7c3aed" } }
        """)!);

        Assert.Equal("#7c3aed", updated["theme"]!["primaryColor"]!.GetValue<string>());
        Assert.Equal(current["pages"]!.ToJsonString(), updated["pages"]!.ToJsonString());
        Assert.Equal(current["views"]!.ToJsonString(), updated["views"]!.ToJsonString());
        Assert.Equal(current["presentation"]!.ToJsonString(), updated["presentation"]!.ToJsonString());
    }

    [Fact]
    public void ReplaceSlice_detail_swaps_the_entity_layout_only()
    {
        var design = DesignAssembler.Assemble(Plan(), Landed(), out _);
        var current = DesignMerge.Apply(Domain(), design, out _).AsObject();
        var plan = DesignPlan.FromDefinition(current)!;

        var updated = current.DeepClone().AsObject();
        DesignAssembler.ReplaceSlice(updated, plan, "detail:ticket", JsonNode.Parse("""
        { "detail": { "entity": "ticket", "blocks": [ { "kind": "fields", "fields": ["subject"] } ] } }
        """)!);

        Assert.Empty(Gate.Validate(updated));
        var ticket = updated["entities"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == "ticket")!;
        Assert.Single(ticket["detail"]!["blocks"]!.AsArray());
        Assert.NotNull(ticket["form"]);                      // an unemitted form keeps the current one
        Assert.Equal(current["pages"]!.ToJsonString(), updated["pages"]!.ToJsonString());
    }
}
