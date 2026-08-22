// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class PlanGateTests
{
    private static JsonObject Domain() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "staffing", "name": "Staffing", "version": "1.0.0",
      "archetype": { "kind": "scheduling", "coreJob": "Assign people to shifts week by week" },
      "entities": [
        { "key": "schedule", "label": "Schedule", "displayField": "name", "fields": [
          { "key": "name", "label": "Name", "type": "text" },
          { "key": "week_start", "label": "Week start", "type": "date" } ] },
        { "key": "assignment", "label": "Assignment", "displayField": "day", "fields": [
          { "key": "schedule", "label": "Schedule", "type": "reference", "targetEntity": "schedule" },
          { "key": "person", "label": "Person", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
          { "key": "day", "label": "Day", "type": "date" } ] },
        { "key": "note", "label": "Note", "ownedBy": { "parent": "schedule", "via": "schedule" }, "fields": [
          { "key": "body", "label": "Body", "type": "longtext" },
          { "key": "schedule", "label": "Schedule", "type": "reference", "targetEntity": "schedule" } ] },
        { "key": "staffing_settings", "label": "Settings", "kind": "settings", "fields": [
          { "key": "default_hours", "label": "Default hours", "type": "integer" } ] }
      ]
    }
    """)!;

    private static DesignPlan Plan() => DesignPlan.Parse(JsonNode.Parse("""
    {
      "theme": { "primaryColor": "#0e7490" },
      "presentation": { "icon": "calendar-account", "color": "#0e7490", "tagline": "Plan the week" },
      "pages": [
        { "key": "schedules", "label": "Schedules", "entity": "schedule", "role": "workspace", "views": [
          { "key": "schedules_table", "label": "All schedules", "type": "table", "entity": "schedule" } ] },
        { "key": "home", "label": "This week", "role": "home", "views": [] },
        { "key": "settings", "label": "Settings", "role": "settings", "views": [] }
      ],
      "details": [ { "entity": "schedule", "form": true, "brief": "The week board lives here" } ],
      "surfaces": [
        { "name": "Week board", "shape": "board", "scope": "per_record", "scopeEntity": "schedule",
          "placement": { "detailOf": "schedule" },
          "axes": { "rows": "person", "columns": "day", "cellEntity": "assignment" },
          "rationale": "You plan one schedule at a time, not all schedules at once" }
      ]
    }
    """))!;

    [Fact]
    public void Parse_orders_home_first_and_settings_last()
    {
        var plan = Plan();
        Assert.Equal(["home", "schedules", "settings"], plan.Pages.Select(p => p.Key));
        Assert.Equal(["home", "workspace", "settings"], plan.Pages.Select(p => p.Role));
    }

    [Fact]
    public void Parse_autoadds_a_detail_for_every_per_record_surface()
    {
        var plan = DesignPlan.Parse(JsonNode.Parse("""
        {
          "pages": [ { "key": "home", "label": "Home", "role": "home", "views": [] } ],
          "details": [],
          "surfaces": [ { "name": "Board", "shape": "board", "scope": "per_record",
            "scopeEntity": "schedule", "placement": {}, "rationale": "r" } ]
        }
        """))!;
        Assert.Single(plan.Details);
        Assert.Equal("schedule", plan.Details[0].Entity);
    }

    [Fact]
    public void Parse_tolerates_junk_and_returns_null_for_no_plan()
    {
        Assert.Null(DesignPlan.Parse(null));
        Assert.Null(DesignPlan.Parse(JsonNode.Parse(""" "not an object" """)));
        var junk = DesignPlan.Parse(JsonNode.Parse("""{ "parameter_name": " " }"""));
        Assert.NotNull(junk);
        Assert.Empty(junk!.Pages);
    }

    [Fact]
    public void A_good_plan_validates_cleanly()
    {
        Assert.Empty(PlanGate.ValidatePlan(Domain(), Plan()));
    }

    [Fact]
    public void Stub_skeleton_is_valid_by_construction()
    {
        Assert.Empty(Gate.Validate(PlanGate.BuildSkeleton(Domain(), Plan())));
    }

    [Fact]
    public void Zero_pages_is_degenerate()
    {
        var plan = DesignPlan.Parse(JsonNode.Parse("""{ "parameter_name": " " }"""))!;
        var errors = PlanGate.ValidatePlan(Domain(), plan);
        Assert.Contains(errors, e => e.Contains("authored no pages"));
    }

    [Fact]
    public void Plan_invariants_are_enforced()
    {
        var noHome = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [ { "key": "a", "label": "A", "role": "workspace", "views": [] } ] }
        """))!;
        Assert.Contains(PlanGate.ValidatePlan(Domain(), noHome), e => e.Contains("role 'home'"));

        var dup = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [
            { "key": "home", "label": "H", "role": "home", "views": [
              { "key": "v1", "label": "V", "type": "table", "entity": "schedule" } ] },
            { "key": "b", "label": "B", "role": "workspace", "views": [
              { "key": "v1", "label": "V", "type": "table", "entity": "schedule" } ] } ] }
        """))!;
        Assert.Contains(PlanGate.ValidatePlan(Domain(), dup), e => e.Contains("duplicate view key 'v1'"));

        var surfaces = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [ { "key": "home", "label": "H", "role": "home", "views": [] } ],
          "surfaces": [
            { "name": "Board", "shape": "board", "scope": "per_record", "rationale": "r" },
            { "name": "Queue", "shape": "queue", "scope": "global", "rationale": "r" } ] }
        """))!;
        var errors = PlanGate.ValidatePlan(Domain(), surfaces);
        Assert.Contains(errors, e => e.Contains("names no scopeEntity"));
        Assert.Contains(errors, e => e.Contains("names no page"));

        var misplaced = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [ { "key": "home", "label": "H", "role": "home", "views": [] } ],
          "details": [ { "entity": "assignment" } ],
          "surfaces": [ { "name": "Board", "shape": "board", "scope": "per_record",
            "scopeEntity": "schedule", "placement": { "detailOf": "assignment" }, "rationale": "r" } ] }
        """))!;
        Assert.Contains(PlanGate.ValidatePlan(Domain(), misplaced),
            e => e.Contains("ITS OWN entity's detail"));
    }

    [Fact]
    public void Skeleton_surfaces_the_subordinate_and_settings_bans()
    {
        var plan = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [
            { "key": "home", "label": "H", "role": "home", "views": [
              { "key": "notes", "label": "Notes", "type": "table", "entity": "note" },
              { "key": "cfg", "label": "Config", "type": "table", "entity": "staffing_settings" } ] } ] }
        """))!;
        var errors = PlanGate.ValidatePlan(Domain(), plan);
        Assert.Contains(errors, e => e.Contains("subordinate"));
        Assert.Contains(errors, e => e.Contains("settings"));
    }

    [Fact]
    public void Unknown_entities_in_the_plan_fail_the_skeleton_gate()
    {
        var plan = DesignPlan.Parse(JsonNode.Parse("""
        { "pages": [ { "key": "home", "label": "H", "role": "home", "views": [
            { "key": "v", "label": "V", "type": "table", "entity": "ghost" } ] } ] }
        """))!;
        Assert.Contains(PlanGate.ValidatePlan(Domain(), plan), e => e.Contains("ghost"));
    }

    private static SliceSpec Spec(DesignPlan plan, string id) =>
        PlanGate.Slices(plan).Single(s => s.Id == id);

    [Fact]
    public void Slices_come_out_in_generation_order_with_their_surfaces()
    {
        var slices = PlanGate.Slices(Plan());
        Assert.Equal(["page:home", "page:schedules", "page:settings", "detail:schedule"],
            slices.Select(s => s.Id));
        var detail = slices.Single(s => s.Id == "detail:schedule");
        Assert.Single(detail.Surfaces);
        Assert.Equal("per_record", detail.Surfaces[0].Scope);
    }

    [Fact]
    public void A_good_page_slice_validates_cleanly()
    {
        var slice = JsonNode.Parse("""
        {
          "page": { "key": "schedules", "label": "Schedules", "entity": "schedule", "blocks": [
            { "kind": "view", "view": "schedules_table" } ] },
          "views": [
            { "key": "schedules_table", "label": "All schedules", "type": "table", "entity": "schedule",
              "config": { "columns": ["name", "week_start"] } } ]
        }
        """);
        Assert.Empty(PlanGate.ValidateSlice(Domain(), Plan(), Spec(Plan(), "page:schedules"), slice));
    }

    [Fact]
    public void Gate_errors_are_attributable_to_the_substituted_slice()
    {
        var slice = JsonNode.Parse("""
        {
          "page": { "key": "schedules", "label": "Schedules", "blocks": [
            { "kind": "view", "view": "schedules_table" } ] },
          "views": [
            { "key": "schedules_table", "label": "All schedules", "type": "table", "entity": "schedule",
              "config": { "columns": ["name", "ghost_field"] } } ]
        }
        """);
        var errors = PlanGate.ValidateSlice(Domain(), Plan(), Spec(Plan(), "page:schedules"), slice);
        Assert.Single(errors);
        Assert.Contains("ghost_field", errors[0]);
    }

    [Fact]
    public void Ownership_a_slice_may_not_author_or_embed_another_screens_views()
    {
        var plan = Plan();
        var foreignAuthor = JsonNode.Parse("""
        {
          "page": { "key": "home", "label": "This week", "blocks": [ { "kind": "text", "value": "hi" } ] },
          "views": [ { "key": "rogue", "label": "Rogue", "type": "table", "entity": "schedule" } ]
        }
        """);
        Assert.Contains(PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "page:home"), foreignAuthor),
            e => e.Contains("not assigned to this screen"));

        var foreignEmbed = JsonNode.Parse("""
        {
          "page": { "key": "home", "label": "This week", "blocks": [
            { "kind": "view", "view": "schedules_table" } ] },
          "views": []
        }
        """);
        Assert.Contains(PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "page:home"), foreignEmbed),
            e => e.Contains("belongs to another"));
    }

    [Fact]
    public void A_page_slice_must_emit_all_its_planned_views_under_its_fixed_key()
    {
        var plan = Plan();
        var missing = JsonNode.Parse("""
        { "page": { "key": "schedules", "label": "S", "blocks": [ { "kind": "text", "value": "x" } ] },
          "views": [] }
        """);
        Assert.Contains(PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "page:schedules"), missing),
            e => e.Contains("'schedules_table' was not emitted"));

        var wrongKey = JsonNode.Parse("""
        { "page": { "key": "sched", "label": "S", "blocks": [ { "kind": "text", "value": "x" } ] },
          "views": [ { "key": "schedules_table", "label": "V", "type": "table", "entity": "schedule" } ] }
        """);
        Assert.Contains(PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "page:schedules"), wrongKey),
            e => e.Contains("must be 'schedules'"));
    }

    [Fact]
    public void Degenerate_slices_report_authored_nothing()
    {
        var plan = Plan();
        foreach (var junk in new[] { JsonNode.Parse("""{ "parameter_name": " " }"""), null })
        {
            var pageErrors = PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "page:home"), junk);
            Assert.Contains(pageErrors, e => e.Contains("authored nothing"));
            var detailErrors = PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "detail:schedule"), junk);
            Assert.Contains(detailErrors, e => e.Contains("authored nothing"));
        }
    }

    [Fact]
    public void The_per_record_machinery_validates_inside_a_detail()
    {
        var plan = Plan();
        var slice = JsonNode.Parse("""
        {
          "detail": { "entity": "schedule", "blocks": [
            { "kind": "hub", "facts": ["week_start"], "actions": ["edit"] },
            { "kind": "repeat", "as": "person", "source": { "platform": "person" }, "blocks": [
              { "kind": "row", "blocks": [
                { "kind": "text", "value": "{{person.full_name}}" },
                { "kind": "repeat", "emptyText": "", "source": { "entity": "assignment", "filters": [
                    { "field": "schedule", "operator": "eq", "value": "{{record.id}}" },
                    { "field": "person", "operator": "eq", "value": "{{person.id}}" } ] },
                  "blocks": [ { "kind": "field", "field": "day" } ] },
                { "kind": "cell", "entity": "assignment", "field": "day", "editable": true,
                  "keys": { "schedule": "{{record.id}}", "person": "{{person.id}}" } } ] } ] },
            { "kind": "child", "entity": "note", "via": "schedule", "childType": "feed", "subordinate": true } ] },
          "form": { "entity": "schedule", "blocks": [
            { "kind": "section", "label": "Schedule", "blocks": [
              { "kind": "fields", "fields": ["name", "week_start"] } ] } ] }
        }
        """);
        Assert.Empty(PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "detail:schedule"), slice));
    }

    [Fact]
    public void A_detail_slice_must_match_its_entity_and_planned_form()
    {
        var plan = Plan();
        var wrongEntity = JsonNode.Parse("""
        { "detail": { "entity": "assignment", "blocks": [ { "kind": "fields", "fields": ["day"] } ] } }
        """);
        var errors = PlanGate.ValidateSlice(Domain(), plan, Spec(plan, "detail:schedule"), wrongEntity);
        Assert.Contains(errors, e => e.Contains("must target entity 'schedule'"));
        Assert.Contains(errors, e => e.Contains("form"));
    }
}
