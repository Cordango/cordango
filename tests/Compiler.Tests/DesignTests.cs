// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>DesignMerge: the design pass can ONLY author presentation-layer keys; the domain
/// must come through byte-identical no matter what the design document contains.</summary>
public class DesignMergeTests
{
    private static JsonObject Domain() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "support", "name": "Support", "version": "1.0.0",
      "entities": [
        { "key": "ticket", "label": "Ticket", "displayField": "subject",
          "fields": [
            { "key": "subject", "label": "Subject", "type": "text", "required": true },
            { "key": "stage", "label": "Stage", "type": "select", "role": "status",
              "options": [ { "value": "open", "label": "Open" }, { "value": "closed", "label": "Closed" } ] }
          ] },
        { "key": "comment", "label": "Comment", "ownedBy": { "parent": "ticket", "via": "ticket" },
          "fields": [
            { "key": "body", "label": "Body", "type": "longtext" },
            { "key": "ticket", "label": "Ticket", "type": "reference", "targetEntity": "ticket" }
          ] }
      ],
      "workflows": [ { "key": "wf", "name": "Notify", "trigger": { "event": "record.created", "entity": "ticket" },
        "effects": [ { "type": "notify", "message": "A new ticket was created." } ] } ]
    }
    """)!;

    private static JsonObject Design() => (JsonObject)JsonNode.Parse("""
    {
      "presentation": { "icon": "ticket", "color": "#0e7490", "tagline": "Track tickets" },
      "views": [
        { "key": "tickets_table", "label": "Tickets", "type": "table", "entity": "ticket",
          "config": { "columns": ["subject", "stage"] } }
      ],
      "pages": [
        { "key": "tickets", "label": "Tickets", "entity": "ticket",
          "blocks": [ { "kind": "view", "view": "tickets_table" } ] }
      ],
      "details": [
        { "entity": "ticket", "blocks": [
          { "kind": "fields", "fields": ["subject", "stage"] },
          { "kind": "child", "entity": "comment", "via": "ticket", "childType": "feed", "subordinate": true }
        ] }
      ]
    }
    """)!;

    [Fact]
    public void A_design_that_authors_navigation_is_ignored_not_failed()
    {
        // `navigation` no longer exists in the language — the sidebar is page order. A model trained
        // on the old shape can still emit one, so the merge must DROP it silently rather than let it
        // reach a document whose root is additionalProperties:false.
        //
        // The resilience this preserves was learned live 2026-07-15: glm authored nav items against
        // PAGE keys, an otherwise-complete design failed the gate on exactly that, and the repair
        // rounds then collapsed. Not copying the section at all is the same protection with no code.
        var design = Design();
        design["navigation"] = JsonNode.Parse("""
        { "type": "sidebar", "items": [
          { "label": "Tickets", "view": "tickets_table" },
          { "label": "Home", "view": "home" }
        ] }
        """);
        var merged = DesignMerge.Apply(Domain(), design, out var issues).AsObject();

        Assert.Empty(issues);                    // silent — no repair round burned
        Assert.Null(merged["navigation"]);       // never copied through
        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void Merges_ui_keys_and_entity_details()
    {
        var merged = DesignMerge.Apply(Domain(), Design(), out var issues).AsObject();
        Assert.Empty(issues);
        Assert.NotNull(merged["views"]);
        Assert.NotNull(merged["pages"]);
        Assert.Equal("ticket", merged["presentation"]!["icon"]!.GetValue<string>());
        var ticket = merged["entities"]![0]!.AsObject();
        Assert.Equal("fields", ticket["detail"]!["blocks"]![0]!["kind"]!.GetValue<string>());
        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void Domain_sections_survive_byte_identical()
    {
        var domain = Domain();
        var before = new
        {
            entities = domain["entities"]!.ToJsonString().Replace("\"detail\"", ""),
            workflows = domain["workflows"]!.ToJsonString(),
            key = domain["key"]!.GetValue<string>(),
        };
        // A hostile design doc trying to smuggle domain edits: unknown top-level keys are ignored,
        // details land only under `detail`.
        var design = Design();
        design["entities"] = JsonNode.Parse("""[{ "key": "evil", "label": "X", "fields": [] }]""");
        design["workflows"] = JsonNode.Parse("[]");
        design["key"] = "hacked";

        var merged = DesignMerge.Apply(domain, design, out _).AsObject();
        Assert.Equal(before.workflows, merged["workflows"]!.ToJsonString());
        Assert.Equal(before.key, merged["key"]!.GetValue<string>());
        var entityKeys = merged["entities"]!.AsArray().Select(e => e!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "ticket", "comment" }, entityKeys);
    }

    [Fact]
    public void Unknown_detail_entity_is_dropped_and_reported()
    {
        var design = Design();
        ((JsonArray)design["details"]!).Add(JsonNode.Parse("""{ "entity": "ghost", "blocks": [ { "kind": "fields" } ] }"""));
        DesignMerge.Apply(Domain(), design, out var issues);
        Assert.Contains(issues, i => i.Contains("unknown entity 'ghost'"));
    }
}

/// <summary>The design layer of the gate: component configs against the catalog contract.</summary>
public class DesignGateTests
{
    private static JsonObject App(string viewsJson, string? pagesJson = null) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "asset", "label": "Asset", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "category", "label": "Category", "type": "select",
              "options": [ { "value": "laptop", "label": "Laptop" } ] },
            { "key": "purchase_value", "label": "Value", "type": "money" },
            { "key": "purchased_at", "label": "Purchased", "type": "date" }
          ] }
      ],
      "views": {{viewsJson}}{{(pagesJson is null ? "" : $", \"pages\": {pagesJson}")}}
    }
    """)!;

    [Fact]
    public void Valid_widget_contract_passes()
    {
        var doc = App("""
        [{ "key": "overview", "label": "Overview", "type": "dashboard", "entity": "asset", "config": { "widgets": [
          { "type": "metric", "source": { "entity": "asset", "aggregate": { "op": "count" } }, "viz": { "title": "Total" } },
          { "type": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "category" } },
            "viz": { "chartType": "donut", "title": "By category" } },
          { "type": "chart", "source": { "entity": "asset", "aggregate": { "op": "sum", "field": "purchase_value", "groupBy": "month_of:purchased_at" } },
            "viz": { "chartType": "bar", "title": "Spend by month" } }
        ] } }]
        """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Unknown_widget_type_is_rejected_with_allowed_list()
    {
        var doc = App("""
        [{ "key": "overview", "label": "Overview", "type": "dashboard", "entity": "asset",
           "config": { "widgets": [ { "type": "stat", "label": "Total Assets", "entity": "asset" } ] } }]
        """);
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("widget type 'stat' is unknown") && e.Contains("metric, chart, list"));
    }

    [Fact]
    public void Widget_field_resolution_and_numeric_rules_enforced()
    {
        var doc = App("""
        [{ "key": "overview", "label": "Overview", "type": "dashboard", "entity": "asset", "config": { "widgets": [
          { "type": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "ghost_field" } },
            "viz": { "chartType": "bar", "title": "Bad group" } },
          { "type": "metric", "source": { "entity": "asset", "aggregate": { "op": "sum", "field": "name" } },
            "viz": { "title": "Bad sum" } }
        ] } }]
        """);
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("groupBy 'ghost_field'"));
        Assert.Contains(errs, e => e.Contains("op 'sum' needs a numeric field"));
    }

    [Fact]
    public void Table_config_columns_must_resolve()
    {
        var doc = App("""
        [{ "key": "t", "label": "Assets", "type": "table", "entity": "asset",
           "config": { "columns": ["name", "nope"] } }]
        """);
        Assert.Contains(Gate.Validate(doc), e => e.Contains("column 'nope'"));
    }

    [Fact]
    public void Detail_blocks_reject_collection_kinds_and_unresolved_fields()
    {
        var doc = App("""[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""");
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [
          { "kind": "view", "view": "t" },
          { "kind": "fields", "fields": ["name", "ghost"] }
        ] }
        """);
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("block kind 'view' is only valid on pages"));
        Assert.Contains(errs, e => e.Contains("unknown field 'ghost'"));
    }

    [Fact]
    public void Pages_reject_record_kinds()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """[{ "key": "p", "label": "Assets", "blocks": [ { "kind": "fields", "fields": ["name"] } ] }]""");
        Assert.Contains(Gate.Validate(doc), e => e.Contains("'fields' is only valid in a record detail"));
    }

    [Fact]
    public void Subordinate_entities_get_no_views_or_pages()
    {
        var doc = App("""[{ "key": "ct", "label": "Comments", "type": "table", "entity": "comment" }]""");
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "comment", "label": "Comment", "ownedBy": { "parent": "asset", "via": "asset" },
          "fields": [
            { "key": "body", "label": "Body", "type": "longtext" },
            { "key": "asset", "label": "Asset", "type": "reference", "targetEntity": "asset" }
          ] }
        """));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("subordinate entity 'comment'"));
    }

    [Fact]
    public void Unknown_block_kind_fails_the_schema()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """[{ "key": "p", "label": "Assets", "blocks": [ { "kind": "hero" } ] }]""");
        Assert.Contains(Gate.Validate(doc), e => e.StartsWith("STRUCTURAL") && e.Contains("blocks/0/kind"));
    }

    [Fact]
    public void Hub_and_tiles_validate_field_references_and_binding()
    {
        var doc = App("""[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""");
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [
          { "kind": "hub", "title": "name", "status": "category", "facts": ["purchase_value", "ghost"] },
          { "kind": "tiles", "tiles": [
            { "label": "Value", "field": "purchase_value", "max": "nope" },
            { "label": "Nothing" }
          ] }
        ] }
        """)!;
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("hub facts field 'ghost'"));
        Assert.Contains(errs, e => e.Contains("max 'nope' is not a field of 'asset'"));
        Assert.Contains(errs, e => e.Contains("tile 'Nothing'") && e.Contains("needs either 'field'"));
        Assert.DoesNotContain(errs, e => e.Contains("status 'category'") && e.Contains("must be a 'select'")); // category IS select
    }

    [Fact]
    public void Hub_is_record_only_and_via_tiles_check_the_child_reference()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """[{ "key": "p", "label": "Assets", "blocks": [ { "kind": "hub" } ] }]""");
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "job", "label": "Job", "ownedBy": { "parent": "asset", "via": "asset" },
          "fields": [
            { "key": "cost", "label": "Cost", "type": "money" },
            { "key": "asset", "label": "Asset", "type": "reference", "targetEntity": "asset" }
          ] }
        """));
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [ { "kind": "tiles", "tiles": [
          { "label": "Jobs", "source": { "entity": "job", "via": "cost", "aggregate": { "op": "count" } } }
        ] } ] }
        """)!;
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("'hub' is only valid in a record detail"));
        Assert.Contains(errs, e => e.Contains("via 'job.cost' must be a reference to 'asset'"));
    }

    [Fact]
    public void Form_layout_rejects_base_and_unknown_fields()
    {
        var doc = App("""[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""");
        doc["entities"]![0]!["form"] = JsonNode.Parse("""
        { "blocks": [
          { "kind": "section", "label": "Basics", "blocks": [
            { "kind": "fields", "fields": ["name", "created_at", "ghost"], "columns": 2 } ] }
        ] }
        """)!;
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("base field 'created_at'"));
        Assert.Contains(errs, e => e.Contains("unknown field 'ghost' on entity 'asset'"));
        Assert.DoesNotContain(errs, e => e.Contains("'name'"));
    }

    [Fact]
    public void Valid_hub_tiles_and_form_pass_the_gate()
    {
        var doc = App("""[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""");
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "job", "label": "Job", "labelPlural": "Jobs", "ownedBy": { "parent": "asset", "via": "asset" },
          "fields": [
            { "key": "cost", "label": "Cost", "type": "money" },
            { "key": "asset", "label": "Asset", "type": "reference", "targetEntity": "asset" }
          ] }
        """));
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [
          { "kind": "hub", "title": "name", "facts": ["category", "purchased_at"], "actions": ["edit", "delete"] },
          { "kind": "tiles", "tiles": [
            { "label": "Maintenance spend", "format": "money",
              "source": { "entity": "job", "via": "asset", "aggregate": { "op": "sum", "field": "cost" } } }
          ] },
          { "kind": "fields", "fields": ["name", "purchase_value"] },
          { "kind": "child", "entity": "job", "via": "asset", "childType": "lineItems", "subordinate": true }
        ] }
        """)!;
        doc["entities"]![0]!["form"] = JsonNode.Parse("""
        { "blocks": [
          { "kind": "section", "label": "Identity", "blocks": [ { "kind": "fields", "fields": ["name", "category"] } ] },
          { "kind": "section", "label": "Purchase", "blocks": [ { "kind": "fields", "fields": ["purchase_value", "purchased_at"], "columns": 2 } ] }
        ] }
        """)!;
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Composed_home_from_primitives_passes_the_gate()
    {
        // A leaderboard (card > repeat > row[...]) + a spotlight stat + a chart — the whole point of M2:
        // a distinctive home composed from generic primitives, not a fixed widgets dashboard.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "card", "label": "Top assets", "blocks": [
                { "kind": "repeat", "source": { "entity": "asset", "sort": [{ "field": "purchase_value", "direction": "desc" }], "limit": 5 },
                  "blocks": [
                    { "kind": "row", "blocks": [
                      { "kind": "text", "value": "#{index}" },
                      { "kind": "avatar", "field": "name" },
                      { "kind": "field", "field": "name", "grow": true },
                      { "kind": "chip", "field": "category" },
                      { "kind": "stat", "field": "purchase_value", "format": "money" }
                    ] }
                  ] }
              ] },
              { "kind": "stat", "source": { "entity": "asset", "aggregate": { "op": "count" } }, "label": "Total assets" },
              { "kind": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "category" } }, "chartType": "donut" }
            ] }]
            """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Primitives_validate_bindings_sources_and_field_refs()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "field", "field": "name" },
              { "kind": "repeat", "source": { "entity": "ghost" }, "blocks": [ { "kind": "text", "value": "x" } ] },
              { "kind": "repeat", "source": { "entity": "asset" }, "blocks": [
                { "kind": "row", "blocks": [
                  { "kind": "field", "field": "ghost" },
                  { "kind": "action", "command": "nope" }
                ] }
              ] },
              { "kind": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "purchase_value" } }, "chartType": "bar" }
            ] }]
            """);
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("'field' requires a record or repeat-item context"));
        Assert.Contains(errs, e => e.Contains("repeat: source entity 'ghost' is unknown"));
        Assert.Contains(errs, e => e.Contains("'field': 'ghost' is not a field of 'asset'"));
        Assert.Contains(errs, e => e.Contains("action command 'nope' is not a command on 'asset'"));
        Assert.Contains(errs, e => e.Contains("chart groupBy 'purchase_value' must be a select/reference/date"));
    }

    // ---- domain.create: the page-level "New <record>" button --------------------------------------

    [Fact]
    public void A_create_button_on_a_page_is_valid()
    {
        // The affordance that did not exist before 2026-08-04. `action` cannot express it — that
        // requires a `command` on the BOUND record, and there is no record yet when you are making
        // one — so a page whose primary surface is a composed grid or a calendar had no way to say
        // "New" at all. The MeetingPrep app shipped a display.text styled to look like a button.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "entity": "asset", "blocks": [
              { "kind": "row", "blocks": [
                { "kind": "create", "entity": "asset", "label": "New asset", "style": "primary" }
              ] },
              { "kind": "view", "view": "t" }
            ] }]
            """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void A_create_button_is_rejected_on_an_unknown_entity_and_inside_a_detail()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "create", "entity": "ghost" }
            ] }]
            """);
        Assert.Contains(Gate.Validate(doc), e => e.Contains("create targets unknown entity 'ghost'"));

        // A record detail is already about ONE record; creating a sibling from there is a child
        // block or a command, not this.
        var detail = App("""[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""");
        detail["entities"]![0]!["detail"] = JsonNode.Parse(
            """{ "blocks": [ { "kind": "create", "entity": "asset" } ] }""");
        Assert.Contains(Gate.Validate(detail), e => e.Contains("'create' belongs on a page"));
    }

    [Fact]
    public void A_create_button_is_rejected_on_a_singleton_entity()
    {
        // There is only ever one settings record, so "New one of these" is meaningless.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "create", "entity": "app_settings" }
            ] }]
            """);
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
            { "key": "app_settings", "label": "Settings", "kind": "settings", "displayField": "name",
              "fields": [ { "key": "name", "label": "Name", "type": "text" } ] }
            """));
        Assert.Contains(Gate.Validate(doc),
            e => e.Contains("create targets 'app_settings'") && e.Contains("nothing to create"));
    }

    // ---- impossible intervals + paired date axes --------------------------------------------------

    [Theory]
    [InlineData("gte", "lt")]    // the 2026-08-02 MeetingPrep pair
    [InlineData("gt", "lte")]
    [InlineData("gt", "lt")]
    public void Range_filters_that_can_never_both_hold_are_rejected(string lo, string hi)
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            $$$"""
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "repeat", "as": "row", "source": { "entity": "asset", "filters": [
                  { "field": "purchased_at", "operator": "{{{lo}}}", "value": "{{day.date}}" },
                  { "field": "purchased_at", "operator": "{{{hi}}}", "value": "{{day.date}}" } ] },
                "blocks": [ { "kind": "text", "value": "x" } ] }
            ] }]
            """);
        Assert.Contains(Gate.Validate(doc), e => e.Contains("can never both be true"));
    }

    [Fact]
    public void A_closed_interval_on_one_value_is_allowed()
    {
        // gte x + lte x is [x,x] — it legitimately matches exactly x, unlike the three above.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "repeat", "as": "row", "source": { "entity": "asset", "filters": [
                  { "field": "purchased_at", "operator": "gte", "value": "2026-08-03" },
                  { "field": "purchased_at", "operator": "lte", "value": "2026-08-03" } ] },
                "blocks": [ { "kind": "text", "value": "x" } ] }
            ] }]
            """);
        Assert.DoesNotContain(Gate.Validate(doc), e => e.Contains("can never both be true"));
    }

    [Fact]
    public void A_half_open_day_bucket_against_the_next_bucket_is_allowed()
    {
        // The shape {{day.next}} exists to make possible.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "repeat", "as": "day", "direction": "row",
                "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
                "blocks": [
                  { "kind": "repeat", "as": "row", "source": { "entity": "asset", "filters": [
                      { "field": "purchased_at", "operator": "gte", "value": "{{day.date}}" },
                      { "field": "purchased_at", "operator": "lt", "value": "{{day.next}}" } ] },
                    "blocks": [ { "kind": "text", "value": "x" } ] } ] }
            ] }]
            """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Paired_date_repeats_in_one_container_must_share_a_direction()
    {
        // The MeetingPrep week grid, reduced to its skeleton: a header strip running in a row above a
        // body repeat that omits `direction` and therefore stacks. Both live under one container.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "stack", "blocks": [
                { "kind": "row", "blocks": [
                  { "kind": "repeat", "as": "hcol", "direction": "row",
                    "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
                    "blocks": [ { "kind": "text", "value": "{{hcol.weekday}}" } ] } ] },
                { "kind": "repeat", "as": "day",
                  "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
                  "blocks": [ { "kind": "text", "value": "{{day.label}}" } ] }
              ] }
            ] }]
            """);
        Assert.Contains(Gate.Validate(doc),
            e => e.Contains("lay out differently") && e.Contains("column vs row"));
    }

    [Fact]
    public void Two_independent_date_repeats_at_the_page_root_are_not_paired()
    {
        // A horizontal week schedule and an unrelated vertical date picker may share a range without
        // being one surface. Pairing is a within-container claim, never a page-wide one.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "repeat", "as": "d1", "direction": "row",
                "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
                "blocks": [ { "kind": "text", "value": "{{d1.weekday}}" } ] },
              { "kind": "repeat", "as": "d2",
                "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
                "blocks": [ { "kind": "text", "value": "{{d2.label}}" } ] }
            ] }]
            """);
        Assert.DoesNotContain(Gate.Validate(doc), e => e.Contains("lay out differently"));
    }

    [Fact]
    public void Block_chart_accepts_month_of_bucketing_over_a_date_field()
    {
        // 'value by month' — the same dialect widgets and the aggregate endpoint already speak.
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "chart", "source": { "entity": "asset",
                  "aggregate": { "op": "sum", "field": "purchase_value", "groupBy": "month_of:purchased_at" } },
                "chartType": "bar" }
            ] }]
            """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Deep_links_validate_page_and_filter_fields()
    {
        // A linked stat + a linked chart on a dashboard page, pointing at a real list page.
        var ok = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "stat", "source": { "entity": "asset", "aggregate": { "op": "count" },
                  "filters": [ { "field": "category", "operator": "eq", "value": "laptop" } ] },
                "label": "Laptops",
                "link": { "page": "assets", "filters": [ { "field": "category", "operator": "eq", "value": "laptop" } ] } },
              { "kind": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "category" } },
                "chartType": "donut", "link": { "page": "assets" } }
            ] },
            { "key": "assets", "label": "Assets", "entity": "asset", "blocks": [ { "kind": "view", "view": "t" } ] }]
            """);
        Assert.Empty(Gate.Validate(ok));

        var bad = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "stat", "source": { "entity": "asset", "aggregate": { "op": "count" } }, "label": "X",
                "link": { "page": "ghost" } },
              { "kind": "chart", "source": { "entity": "asset", "aggregate": { "op": "count", "groupBy": "category" } },
                "chartType": "bar", "link": { "page": "assets", "filters": [ { "field": "nonesuch", "operator": "eq", "value": "x" } ] } }
            ] },
            { "key": "assets", "label": "Assets", "entity": "asset", "blocks": [ { "kind": "view", "view": "t" } ] }]
            """);
        var errs = Gate.Validate(bad);
        Assert.Contains(errs, e => e.Contains("link page 'ghost' is not a page"));
        Assert.Contains(errs, e => e.Contains("link filter field 'nonesuch'"));
    }

    [Fact]
    public void Block_chart_month_of_requires_a_date_field()
    {
        var doc = App(
            """[{ "key": "t", "label": "Assets", "type": "table", "entity": "asset" }]""",
            """
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "chart", "source": { "entity": "asset",
                  "aggregate": { "op": "count", "groupBy": "month_of:category" } }, "chartType": "bar" }
            ] }]
            """);
        Assert.Contains(Gate.Validate(doc), e => e.Contains("month_of:category") && e.Contains("date/datetime"));
    }
}
