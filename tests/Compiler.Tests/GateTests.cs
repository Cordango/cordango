// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class GateTests
{
    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;

    private static JsonObject Minimal() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "thing", "label": "Thing", "displayField": "name",
          "fields": [{ "key": "name", "label": "Name", "type": "text" }] }
      ]
    }
    """)!;

    [Fact]
    public void Crm_example_is_fully_valid() =>
        Assert.Empty(Gate.Validate(Fixture("crm.appdef.json")));

    [Fact]
    public void Platform_entities_definition_is_valid() =>
        Assert.Empty(Gate.Validate(Fixture("platform-entities.json")));

    [Fact]
    public void Minimal_is_valid() =>
        Assert.Empty(Gate.Validate(Minimal()));

    [Fact]
    public void Archetype_is_optional_and_validates_when_present()
    {
        var doc = Minimal();
        doc["archetype"] = JsonNode.Parse(
            """{ "kind": "scheduling", "coreJob": "Assign employees to station shifts week by week" }""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Archetype_with_unknown_kind_is_rejected_with_the_allowed_values()
    {
        var doc = Minimal();
        doc["archetype"] = JsonNode.Parse("""{ "kind": "spreadsheet", "coreJob": "whatever" }""");
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.StartsWith("STRUCTURAL") && e.Contains("scheduling") && e.Contains("approval"));
    }

    private static JsonObject WithSelfTargetCommand()
    {
        var doc = Minimal();
        doc["commands"] = JsonNode.Parse("""
        [ { "key": "close_thing", "label": "Close", "entity": "thing",
            "effects": [ { "type": "updateRecord", "target": "self", "set": { "name": "closed" } } ] } ]
        """);
        return doc;
    }

    [Fact]
    public void A_real_error_does_not_flood_phantom_oneOf_branch_errors()
    {
        var doc = WithSelfTargetCommand();
        Assert.Empty(Gate.Validate(doc));

        doc["entities"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "key": "amount", "label": "Amount", "type": "number" }"""));
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("/fields/1/type"));
        Assert.DoesNotContain(errs, e => e.Contains("target"));
    }

    [Fact]
    public void A_value_matching_no_oneOf_branch_still_reports_its_errors()
    {
        var doc = WithSelfTargetCommand();
        doc["commands"]![0]!["effects"]![0]!["target"] = JsonNode.Parse("""{ "bogus": true }""");
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.Contains("target"));
    }

    [Fact]
    public void Structural_catches_bad_type_and_key()
    {
        var errs = Gate.Validate(Fixture("broken.appdef.json"));
        Assert.NotEmpty(errs);
        Assert.All(errs, e => Assert.StartsWith("STRUCTURAL", e));
        Assert.Contains(errs, e => e.Contains("fields/2"));
        Assert.Contains(errs, e => e.Contains("fields/3"));
    }

    [Fact]
    public void Reference_to_unknown_entity_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "owner", "label": "Owner", "type": "reference", "targetEntity": "ghost" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("references unknown entity 'ghost'"));
    }

    [Fact]
    public void Display_field_must_exist()
    {
        var doc = Minimal();
        doc["entities"]![0]!["displayField"] = "nope";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("displayField 'nope'"));
    }

    [Fact]
    public void Declaring_base_field_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "record_state", "label": "State", "type": "text" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("reserved base field 'record_state'"));
    }

    [Fact]
    public void A_business_field_named_status_is_allowed()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "status", "label": "Status", "type": "text" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Workflow_when_guard_requires_a_condition_language()
    {
        var doc = Minimal();
        doc["workflows"] = JsonNode.Parse("""
        [{ "key": "wf", "name": "WF",
           "trigger": { "event": "schedule", "cron": "0 8 * * *", "entity": "thing" },
           "when": { "field": "ghost", "operator": "isNotEmpty" },
           "effects": [{ "type": "notify", "message": "hi" }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("condition field 'ghost'"));
    }

    private static JsonObject WithForEach(string set, string source = """
        { "entity": "row", "filters": [] }
        """)
    {
        var doc = (JsonObject)JsonNode.Parse("""
        {
          "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
          "entities": [
            { "key": "thing", "label": "Thing", "displayField": "name",
              "fields": [
                { "key": "name", "label": "Name", "type": "text" },
                { "key": "plan_start", "label": "Start", "type": "date" },
                { "key": "months", "label": "Months", "type": "integer" }
              ] },
            { "key": "row", "label": "Row", "displayField": "label",
              "fields": [{ "key": "label", "label": "Label", "type": "text" }] },
            { "key": "cell", "label": "Cell", "displayField": "title",
              "fields": [
                { "key": "title", "label": "Title", "type": "text" },
                { "key": "at", "label": "At", "type": "date" },
                { "key": "n", "label": "N", "type": "integer" }
              ] }
          ]
        }
        """)!;

        doc["workflows"] = JsonNode.Parse($$"""
        [{ "key": "wf", "name": "WF",
           "trigger": { "event": "record.created", "entity": "thing" },
           "effects": [{ "type": "createForEach", "entity": "cell",
             "source": {{source}},
             "set": {{set}} }] }]
        """);

        return doc;
    }

    [Fact]
    public void A_for_each_set_reading_the_iterated_row_is_accepted() =>
        Assert.Empty(Gate.SemanticErrors(WithForEach("""
            { "title": "{{record.name}} {{source.label}}" }
            """)));

    [Fact]
    public void A_for_each_set_reading_a_field_the_iterated_entity_does_not_have_is_refused() =>
        Assert.Contains(
            Gate.SemanticErrors(WithForEach("""
                { "title": "{{source.min_rest_minutes}}" }
                """)),
            e => e.Contains("the entity being iterated"));

    [Fact]
    public void A_for_each_set_key_that_is_not_a_field_is_refused() =>
        Assert.Contains(
            Gate.SemanticErrors(WithForEach("""
                { "nonesuch": "{{source.label}}" }
                """)),
            e => e.Contains("set 'nonesuch' is not a field"));

    [Fact]
    public void A_for_each_over_a_range_offers_index_date_and_end() =>
        Assert.Empty(Gate.SemanticErrors(WithForEach("""
            { "title": "{{source.index}}", "at": "{{source.date}}" }
            """, """
            { "range": { "from": "{{record.plan_start}}", "count": "{{record.months}}", "step": "month" } }
            """)));

    [Fact]
    public void A_for_each_over_a_range_has_no_other_columns() =>
        Assert.Contains(
            Gate.SemanticErrors(WithForEach("""
                { "title": "{{source.label}}" }
                """, """
                { "range": { "from": "{{record.plan_start}}", "count": "{{record.months}}", "step": "month" } }
                """)),
            e => e.Contains("a generated date row has"));

    [Fact]
    public void A_source_token_outside_a_for_each_is_refused()
    {
        var doc = Minimal();
        doc["workflows"] = JsonNode.Parse("""
        [{ "key": "wf", "name": "WF",
           "trigger": { "event": "record.created", "entity": "thing" },
           "effects": [{ "type": "notify", "message": "{{source.anything}}" }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("there is no source row here"));
    }

    [Fact]
    public void Reference_to_platform_entity_resolves()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "owner", "label": "Owner", "type": "reference", "targetApp": "platform", "targetEntity": "person" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Reference_to_unknown_platform_entity_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "owner", "label": "Owner", "type": "reference", "targetApp": "platform", "targetEntity": "ghost" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("unknown platform entity 'ghost'"));
    }

    [Fact]
    public void Cross_app_reference_is_skipped_not_errored()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "ref", "label": "Ref", "type": "reference", "targetApp": "other_app", "targetEntity": "whatever" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Reference_to_core_app_entity_resolves()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "customer", "label": "Customer", "type": "reference", "targetApp": "core_organizations", "targetEntity": "organization" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Reference_to_unknown_entity_in_core_app_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "customer", "label": "Customer", "type": "reference", "targetApp": "core_organizations", "targetEntity": "ghost" }"""));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("unknown entity 'ghost' in core app 'core_organizations'"));
    }

    [Fact]
    public void Core_reference_names_the_field_that_is_wrong()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "customer", "label": "Customer", "type": "reference", "targetApp": "core_organizations", "targetEntity": "ghost" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'thing.customer'"));
    }

    [Fact]
    public void OwnedBy_via_a_cross_app_reference_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "line", "label": "Line", "labelPlural": "Lines", "displayField": "name",
          "ownedBy": { "parent": "thing", "via": "org" },
          "fields": [ { "key": "name", "label": "Name", "type": "text" },
                      { "key": "org", "label": "Org", "type": "reference",
                        "targetApp": "core_organizations", "targetEntity": "organization" } ] }
        """));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("ownedBy.via 'org' points into 'core_organizations'"));
    }

    [Fact]
    public void Status_role_on_select_field_is_valid()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse("""
            { "key": "stage", "label": "Stage", "type": "select", "role": "status",
              "options": [{ "value": "open", "label": "Open" }, { "value": "done", "label": "Done" }] }
            """));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Status_role_on_non_select_field_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "stage", "label": "Stage", "type": "text", "role": "status" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("role 'status'") && e.Contains("only valid on a 'select'"));
    }

    [Fact]
    public void Multiple_status_role_fields_on_one_entity_are_rejected()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "stage", "label": "Stage", "type": "select", "role": "status", "options": [{ "value": "a", "label": "A" }] }"""));
        fields.Add(JsonNode.Parse("""{ "key": "phase", "label": "Phase", "type": "select", "role": "status", "options": [{ "value": "b", "label": "B" }] }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("multiple role:'status' fields"));
    }

    [Fact]
    public void Unconfirmed_role_on_a_non_json_field_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "pending", "label": "Pending", "type": "text", "role": "unconfirmed" }"""));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("role 'unconfirmed'") && e.Contains("'json' field"));
    }

    [Fact]
    public void Multiple_unconfirmed_role_fields_on_one_entity_are_rejected()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "pending", "label": "P", "type": "json", "role": "unconfirmed" }"""));
        fields.Add(JsonNode.Parse("""{ "key": "unsure", "label": "U", "type": "json", "role": "unconfirmed" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("multiple role:'unconfirmed' fields"));
    }

    [Fact]
    public void A_computed_unconfirmed_marker_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse("""
            { "key": "pending", "label": "Pending", "type": "json", "role": "unconfirmed",
              "computed": { "expr": "1" } }
        """));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("role 'unconfirmed'") && e.Contains("computed"));
    }

    [Fact]
    public void A_json_unconfirmed_marker_is_accepted()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "pending", "label": "Pending", "type": "json", "role": "unconfirmed" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Differs_role_on_a_non_json_field_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "found", "label": "Found", "type": "text", "role": "differs" }"""));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("role 'differs'") && e.Contains("'json' field"));
    }

    [Fact]
    public void Multiple_differs_role_fields_on_one_entity_are_rejected()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "found", "label": "F", "type": "json", "role": "differs" }"""));
        fields.Add(JsonNode.Parse("""{ "key": "other", "label": "O", "type": "json", "role": "differs" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("multiple role:'differs' fields"));
    }

    [Fact]
    public void A_computed_differs_marker_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse("""
            { "key": "found", "label": "Found", "type": "json", "role": "differs",
              "computed": { "expr": "1" } }
        """));
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("role 'differs'") && e.Contains("computed"));
    }

    [Fact]
    public void Both_marks_on_one_entity_are_accepted()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "pending", "label": "P", "type": "json", "role": "unconfirmed" }"""));
        fields.Add(JsonNode.Parse("""{ "key": "found", "label": "F", "type": "json", "role": "differs" }"""));
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void RelatedApps_on_a_collection_surface_is_rejected()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse("""
            [ { "key": "home", "title": "Home", "blocks": [ { "kind": "relatedApps" } ] } ]
        """);
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("'relatedApps'") && e.Contains("record"));
    }

    [Fact]
    public void RelatedApps_on_a_record_detail_needs_no_configuration()
    {
        var doc = Minimal();
        doc["entities"]![0]!["detail"] = new JsonObject
        {
            ["blocks"] = new JsonArray(JsonNode.Parse("""{ "kind": "relatedApps" }""")),
        };
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void RelatedApps_is_not_offered_to_the_generator()
    {
        Assert.Contains("relatedApps", BlockKinds.NotAuthorable);
        Assert.DoesNotContain(BlockKinds.All, k => k.Canonical == "relatedApps");
    }

    private const string Embed1 = """{ "kind": "externalEmbed", "key": "share_price", "provider": "tradingview_mini" }""";
    private const string Embed2 = """{ "kind": "externalEmbed", "key": "peer_chart", "provider": "tradingview_mini" }""";

    private static JsonObject WithDetail(string blocksJson)
    {
        var doc = Minimal();
        doc["entities"]![0]!["detail"] = JsonNode.Parse($$"""{ "blocks": [{{blocksJson}}] }""");
        return doc;
    }

    [Fact]
    public void An_external_embed_on_a_page_is_rejected()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse($$"""
            [ { "key": "home", "title": "Home", "blocks": [ {{Embed1}} ] } ]
        """);

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("externalEmbed") && e.Contains("record detail"));
    }

    [Fact]
    public void Two_external_embeds_on_one_entity_may_not_share_a_key()
    {
        var doc = WithDetail($$"""
            { "kind": "tabs", "tabs": [
                { "label": "One", "blocks": [ {{Embed1}} ] },
                { "label": "Two", "blocks": [ {{Embed1}} ] } ] }
        """);

        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("share the key 'share_price'"));
    }

    [Fact]
    public void Two_external_embeds_with_distinct_keys_are_accepted()
    {
        Assert.Empty(Gate.SemanticErrors(WithDetail($"{Embed1}, {Embed2}")));
    }

    [Fact]
    public void Start_and_due_roles_on_date_fields_are_valid()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "start_date", "label": "Start", "type": "date", "role": "start" }"""));
        fields.Add(JsonNode.Parse("""{ "key": "due_date", "label": "Due", "type": "datetime", "role": "due" }"""));
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Start_role_on_non_date_field_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "kickoff", "label": "Kickoff", "type": "text", "role": "start" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("role 'start'") && e.Contains("'date'/'datetime'"));
    }

    [Fact]
    public void Multiple_due_role_fields_on_one_entity_are_rejected()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""{ "key": "due_date", "label": "Due", "type": "date", "role": "due" }"""));
        fields.Add(JsonNode.Parse("""{ "key": "deadline", "label": "Deadline", "type": "date", "role": "due" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("multiple role:'due' fields"));
    }

    [Fact]
    public void Option_phase_is_structurally_valid_and_unknown_phase_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse("""
            { "key": "stage", "label": "Stage", "type": "select", "role": "status",
              "options": [{ "value": "open", "label": "Open", "phase": "not_started" },
                          { "value": "done", "label": "Done", "phase": "done" }] }
            """));
        Assert.Empty(Gate.Validate(doc));

        doc["entities"]![0]!["fields"]![1]!["options"]![0]!["phase"] = "finished";
        Assert.Contains(Gate.Validate(doc), e => e.StartsWith("STRUCTURAL") && e.Contains("phase"));
    }

    [Fact]
    public void TreeAggregate_on_numeric_is_valid_and_on_text_is_rejected()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "effort", "label": "Effort", "type": "decimal", "treeAggregate": "sum" }"""));
        Assert.Empty(Gate.Validate(doc));

        doc["entities"]![0]!["fields"]![0]!["treeAggregate"] = "sum";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("treeAggregate") && e.Contains("'integer'/'decimal'/'money'"));
    }

    private static JsonObject WithChildTable()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "bucket", "label": "Bucket", "labelPlural": "Buckets", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text", "required": true },
            { "key": "order", "label": "Order", "type": "integer" }
          ] }
        """));
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
        { "key": "item", "label": "Item", "labelPlural": "Items", "displayField": "title",
          "fields": [
            { "key": "title", "label": "Title", "type": "text" },
            { "key": "thing", "label": "Thing", "type": "reference", "targetEntity": "thing" },
            { "key": "bucket", "label": "Bucket", "type": "reference", "targetEntity": "bucket", "onDelete": "setNull" },
            { "key": "kind", "label": "Kind", "type": "select",
              "options": [{ "value": "a", "label": "A" }, { "value": "b", "label": "B" }] }
          ] }
        """));
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [
            { "kind": "child", "entity": "item", "via": "thing", "childType": "table",
              "fields": ["title", "kind"],
              "filterBar": { "search": ["title"], "facets": ["kind"] } }
        ] }
        """);
        return doc;
    }

    [Fact]
    public void Child_table_with_fields_and_filterBar_is_valid() =>
        Assert.Empty(Gate.Validate(WithChildTable()));

    [Fact]
    public void Child_filterBar_facet_on_text_field_is_rejected()
    {
        var doc = WithChildTable();
        doc["entities"]![0]!["detail"]!["blocks"]![0]!["filterBar"]!["facets"]![0] = "title";
        Assert.Contains(Gate.Validate(doc),
            e => e.Contains("filterBar facet 'title'") && e.Contains("select/multiselect/reference"));
    }

    [Fact]
    public void Child_filterBar_search_field_must_exist()
    {
        var doc = WithChildTable();
        doc["entities"]![0]!["detail"]!["blocks"]![0]!["filterBar"]!["search"]![0] = "nope";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("filterBar search field 'nope'"));
    }

    [Fact]
    public void Child_curated_fields_must_exist()
    {
        var doc = WithChildTable();
        doc["entities"]![0]!["detail"]!["blocks"]![0]!["fields"]![0] = "bogus";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("child column 'bogus'"));
    }

    private static JsonObject ChildGroupBy(JsonObject doc) =>
        (JsonObject)doc["entities"]![0]!["detail"]!["blocks"]![0]!.AsObject()!;

    [Fact]
    public void Child_groupBy_reference_with_orderBy_is_valid()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["groupBy"] = JsonNode.Parse("""{ "field": "bucket", "orderBy": "order", "ungroupedLabel": "(No bucket)" }""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Child_groupBy_select_is_valid_but_rejects_orderBy()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["groupBy"] = JsonNode.Parse("""{ "field": "kind" }""");
        Assert.Empty(Gate.Validate(doc));

        ChildGroupBy(doc)["groupBy"] = JsonNode.Parse("""{ "field": "kind", "orderBy": "order" }""");
        Assert.Contains(Gate.Validate(doc), e => e.Contains("orderBy is only valid when the field is a reference"));
    }

    [Fact]
    public void Child_groupBy_text_field_is_rejected()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["groupBy"] = JsonNode.Parse("""{ "field": "title" }""");
        Assert.Contains(Gate.Validate(doc), e => e.Contains("groupBy field 'item.title'") && e.Contains("select") && e.Contains("reference"));
    }

    [Fact]
    public void Child_groupBy_orderBy_must_resolve_on_the_referenced_entity()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["groupBy"] = JsonNode.Parse("""{ "field": "bucket", "orderBy": "rank" }""");
        Assert.Contains(Gate.Validate(doc), e => e.Contains("groupBy orderBy 'rank'") && e.Contains("'bucket'"));
    }

    [Fact]
    public void Child_orderField_on_numeric_field_is_valid()
    {
        var doc = WithChildTable();
        ((JsonArray)doc["entities"]![2]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "position", "label": "Position", "type": "decimal" }"""));
        ChildGroupBy(doc)["orderField"] = "position";
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Child_orderField_on_text_field_is_rejected()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["orderField"] = "title";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("orderField 'item.title'") && e.Contains("'integer' or 'decimal'"));
    }

    [Fact]
    public void Child_orderField_must_exist()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["orderField"] = "position";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("orderField 'position' is not a field of 'item'"));
    }

    [Fact]
    public void Table_view_config_filterBar_fields_must_resolve()
    {
        var doc = WithChildTable();
        doc["views"] = JsonNode.Parse("""
        [ { "key": "items", "label": "Items", "type": "table", "entity": "item",
            "config": { "columns": ["title", "kind"],
                        "filterBar": { "search": ["nope"], "facets": ["title"] } } } ]
        """);
        var errs = Gate.Validate(doc);
        Assert.Contains(errs, e => e.StartsWith("DESIGN") && e.Contains("filterBar search field 'nope'"));
        Assert.Contains(errs, e => e.StartsWith("DESIGN") && e.Contains("filterBar facet 'title'") && e.Contains("select/multiselect/reference"));
    }

    [Fact]
    public void InlineEdit_is_accepted_on_child_and_table_view_config()
    {
        var doc = WithChildTable();
        ChildGroupBy(doc)["inlineEdit"] = true;
        doc["views"] = JsonNode.Parse("""
        [ { "key": "items", "label": "Items", "type": "table", "entity": "item",
            "config": { "columns": ["title", "kind"], "inlineEdit": true } } ]
        """);
        Assert.Empty(Gate.Validate(doc));
    }

    private static JsonObject WithBoardAndGantt()
    {
        var doc = WithChildTable();
        var item = ((JsonArray)doc["entities"]!).OfType<JsonObject>().First(e => (string?)e["key"] == "item");
        ((JsonArray)item["fields"]!).Add(JsonNode.Parse("""{ "key": "starts", "label": "Starts", "type": "date" }"""));
        ((JsonArray)item["fields"]!).Add(JsonNode.Parse("""{ "key": "ends", "label": "Ends", "type": "date" }"""));
        doc["entities"]![0]!["detail"] = JsonNode.Parse("""
        { "blocks": [
            { "kind": "board", "source": { "entity": "item", "via": "thing" },
              "groupField": "kind", "cardFields": ["title"], "openDetail": true },
            { "kind": "gantt", "source": { "entity": "item", "via": "thing" },
              "startField": "starts", "endField": "ends", "labelField": "title", "colorField": "kind",
              "milestones": { "source": { "entity": "bucket" }, "dateField": "when", "labelField": "name" } }
        ] }
        """);
        var bucket = ((JsonArray)doc["entities"]!).OfType<JsonObject>().First(e => (string?)e["key"] == "bucket");
        ((JsonArray)bucket["fields"]!).Add(JsonNode.Parse("""{ "key": "when", "label": "When", "type": "date" }"""));
        return doc;
    }

    [Fact]
    public void Board_and_gantt_in_a_detail_validate() =>
        Assert.Empty(Gate.Validate(WithBoardAndGantt()));

    [Fact]
    public void Board_groupField_must_be_select_or_reference()
    {
        var doc = WithBoardAndGantt();
        doc["entities"]![0]!["detail"]!["blocks"]![0]!["groupField"] = "title";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("board groupField 'title'") && e.Contains("select or reference"));
    }

    [Fact]
    public void Board_via_must_reference_the_bound_entity()
    {
        var doc = WithBoardAndGantt();
        doc["entities"]![0]!["detail"]!["blocks"]![0]!["source"]!["via"] = "bucket";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("via 'item.bucket' must be a reference to 'thing'"));
    }

    [Fact]
    public void Gantt_startField_must_be_a_date()
    {
        var doc = WithBoardAndGantt();
        doc["entities"]![0]!["detail"]!["blocks"]![1]!["startField"] = "title";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("gantt startField 'title'") && e.Contains("date field"));
    }

    [Fact]
    public void Gantt_milestones_dateField_must_be_a_date()
    {
        var doc = WithBoardAndGantt();
        doc["entities"]![0]!["detail"]!["blocks"]![1]!["milestones"]!["dateField"] = "name";
        Assert.Contains(Gate.Validate(doc), e => e.Contains("gantt milestones dateField must be a date field"));
    }

    private static JsonObject ProcessDoc() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0",
      "entities":[
        { "key":"expense","label":"Expense","displayField":"title",
          "fields":[
            {"key":"title","label":"Title","type":"text"},
            {"key":"reason","label":"Reason","type":"longtext"},
            {"key":"stage","label":"Stage","type":"select","role":"status"}
          ] }
      ],
      "processes":[
        { "key":"approval","entity":"expense","stateField":"stage","initialState":"draft",
          "states":[{"key":"draft","label":"Draft"},{"key":"submitted","label":"Submitted"},{"key":"approved","label":"Approved","terminal":true}],
          "transitions":[
            {"key":"submit","label":"Submit","from":["draft"],"to":"submitted","command":"submit_expense"},
            {"key":"approve","label":"Approve","from":["submitted"],"to":"approved","command":"approve_expense"}
          ] }
      ],
      "commands":[
        {"key":"submit_expense","label":"Submit","entity":"expense","effects":[]},
        {"key":"approve_expense","label":"Approve","entity":"expense",
         "effects":[{"type":"notify","to":"{{actor.id}}","message":"Approved {{record.title}}"}]}
      ]
    }
    """)!;

    [Fact]
    public void Process_and_commands_are_valid() => Assert.Empty(Gate.Validate(ProcessDoc()));

    [Fact]
    public void A_state_nothing_transitions_into_is_rejected()
    {
        var doc = ProcessDoc();
        doc["entities"]![0]!["fields"] = JsonNode.Parse("""
            [{"key":"title","label":"Title","type":"text"},
             {"key":"action_status","label":"Status","type":"select","role":"status"}]
            """);
        doc["commands"] = new JsonArray();
        doc["processes"] = JsonNode.Parse("""
        [{ "key":"action_item_lifecycle","entity":"expense","stateField":"action_status",
           "initialState":"open",
           "states":[
             {"key":"open","label":"Open","phase":"active"},
             {"key":"reopened","label":"Reopened","phase":"active"},
             {"key":"closed","label":"Closed","phase":"done","terminal":true}],
           "transitions":[
             {"key":"close_action_item","label":"Close Action Item","from":["open","reopened"],"to":"closed"},
             {"key":"reopen_action_item","label":"Reopen Action Item","from":["reopened"],"to":"open"}] }]
        """);

        var errs = Gate.SemanticErrors(doc);
        Assert.Contains(errs, e => e.Contains("state 'reopened' can never be reached"));
        Assert.Contains(errs, e => e.Contains("transition 'reopen_action_item' can never fire"));
        Assert.DoesNotContain(errs, e => e.Contains("transition 'close_action_item'"));
    }

    [Fact]
    public void A_disconnected_cycle_is_rejected_even_though_each_state_has_an_incoming_transition()
    {
        var doc = ProcessDoc();
        var states = (JsonArray)doc["processes"]![0]!["states"]!;
        states.Add(JsonNode.Parse("""{"key":"ghost_a","label":"Ghost A"}"""));
        states.Add(JsonNode.Parse("""{"key":"ghost_b","label":"Ghost B"}"""));
        var trans = (JsonArray)doc["processes"]![0]!["transitions"]!;
        trans.Add(JsonNode.Parse("""{"key":"a_to_b","label":"A","from":["ghost_a"],"to":"ghost_b"}"""));
        trans.Add(JsonNode.Parse("""{"key":"b_to_a","label":"B","from":["ghost_b"],"to":"ghost_a"}"""));

        var errs = Gate.SemanticErrors(doc);
        Assert.Contains(errs, e => e.Contains("state 'ghost_a' can never be reached"));
        Assert.Contains(errs, e => e.Contains("state 'ghost_b' can never be reached"));
    }

    [Fact]
    public void A_conditional_initial_state_counts_as_an_entry()
    {
        var doc = ProcessDoc();
        var p = (JsonObject)doc["processes"]![0]!;
        ((JsonArray)p["states"]!).Add(JsonNode.Parse("""{"key":"auto","label":"Auto"}"""));
        p["initialState"] = JsonNode.Parse("""
            { "rules":[{"when":{"field":"title","operator":"eq","value":"x"},"state":"auto"}],
              "fallback":"draft" }
            """);

        Assert.DoesNotContain(Gate.SemanticErrors(doc), e => e.Contains("can never be reached"));
    }

    [Fact]
    public void Reachability_is_not_reported_on_top_of_a_broken_entry()
    {
        var doc = ProcessDoc();
        doc["processes"]![0]!["initialState"] = "nonexistent";

        var errs = Gate.SemanticErrors(doc);
        Assert.Contains(errs, e => e.Contains("initialState 'nonexistent' is not one of its states"));
        Assert.DoesNotContain(errs, e => e.Contains("can never be reached"));
    }

    [Fact]
    public void Command_on_unknown_entity_is_rejected()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]!).Add(JsonNode.Parse("""{ "key":"x","label":"X","entity":"ghost","effects":[{"type":"notify","message":"hi"}] }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("command 'x' targets unknown entity 'ghost'"));
    }

    [Fact]
    public void Command_with_empty_effects_and_no_transition_is_rejected()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]!).Add(JsonNode.Parse("""{ "key":"noop","label":"Noop","entity":"expense","effects":[] }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("command 'noop' has no effects"));
    }

    [Fact]
    public void A_process_governed_field_must_not_also_author_its_options()
    {
        var doc = ProcessDoc();
        ((JsonObject)doc["entities"]![0]!["fields"]![2]!)["options"] = JsonNode.Parse(
            """[{"value":"draft","label":"Draft"},{"value":"submitted","label":"Submitted"},{"value":"approved","label":"Approved"}]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("remove the field's 'options'"));
    }

    [Fact]
    public void A_process_governed_field_must_not_author_initial_rules()
    {
        var doc = ProcessDoc();
        ((JsonObject)doc["entities"]![0]!["fields"]![2]!)["initial"] = JsonNode.Parse(
            """[{"when":{"field":"title","operator":"isNotEmpty"},"value":"submitted"}]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("move the field's 'initial' rules to initialState"));
    }

    [Fact]
    public void Conditional_process_entry_is_accepted_and_its_states_are_checked()
    {
        var doc = ProcessDoc();
        var process = (JsonObject)doc["processes"]![0]!;
        process["initialState"] = JsonNode.Parse("""
        { "rules":[{"when":{"field":"reason","operator":"isNotEmpty"},"state":"submitted"}],
          "fallback":"draft" }
        """);
        Assert.Empty(Gate.Validate(doc));

        process["initialState"]!["fallback"] = "ghost";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("initialState fallback 'ghost' is not one of its states"));
        process["initialState"]!["fallback"] = "draft";

        process["initialState"]!["rules"]![0]!["state"] = "ghost";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("initialState rule[0] state 'ghost' is not one of its states"));
        process["initialState"]!["rules"]![0]!["state"] = "submitted";

        process["initialState"]!["rules"]![0]!["when"] = JsonNode.Parse("""{"field":"nope","operator":"eq","value":1}""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("initialState rule[0] guard field 'nope' is not a field of 'expense'"));
    }

    [Fact]
    public void A_process_governed_field_must_not_also_author_its_default()
    {
        var doc = ProcessDoc();
        ((JsonObject)doc["entities"]![0]!["fields"]![2]!)["default"] = "draft";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("remove the field's 'default'"));
    }

    [Fact]
    public void An_ungoverned_select_still_needs_its_options()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{"key":"category","label":"Category","type":"select"}"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'expense.category' is a select with no options"));
    }

    [Fact]
    public void A_governed_fields_states_are_valid_values_everywhere_options_would_be()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]!).Add(JsonNode.Parse("""
        { "key":"force_approve","label":"Force","entity":"expense",
          "effects":[{"type":"updateRecord","set":{"stage":"approved"}}] }
        """));
        Assert.Empty(Gate.SemanticErrors(doc));

        ((JsonArray)doc["commands"]!).Add(JsonNode.Parse("""
        { "key":"bogus","label":"Bogus","entity":"expense",
          "effects":[{"type":"updateRecord","set":{"stage":"not_a_state"}}] }
        """));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("is not a valid option of the 'stage' select"));
    }

    [Fact]
    public void Process_stateField_must_be_the_status_role_field()
    {
        var doc = ProcessDoc();
        ((JsonObject)doc["entities"]![0]!["fields"]![2]!).Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("no role:'status' field"));
    }

    [Fact]
    public void Two_processes_on_one_entity_are_rejected()
    {
        var doc = ProcessDoc();
        var p2 = JsonNode.Parse("""{ "key":"approval2","entity":"expense","stateField":"stage","initialState":"draft","states":[{"key":"draft","label":"D"},{"key":"submitted","label":"S"},{"key":"approved","label":"A"}],"transitions":[] }""");
        ((JsonArray)doc["processes"]!).Add(p2);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("more than one process"));
    }

    [Fact]
    public void Transition_leaving_a_terminal_state_is_rejected()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["processes"]![0]!["transitions"]!).Add(JsonNode.Parse(
            """{ "key":"reopen","from":["approved"],"to":"draft","command":"submit_expense" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("leaves terminal state 'approved'"));
    }

    [Fact]
    public void Grant_of_unknown_command_is_rejected()
    {
        var doc = ProcessDoc();
        doc["roles"] = JsonNode.Parse("""[{ "key":"mgr","name":"Manager","grants":[{ "entity":"expense","read":true,"commands":["ghost_cmd"] }] }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("grants unknown command 'ghost_cmd'"));
    }

    [Fact]
    public void Wildcard_grant_may_only_grant_all_commands()
    {
        var doc = ProcessDoc();
        doc["roles"] = JsonNode.Parse("""[{ "key":"admin","name":"Admin","grants":[{ "entity":"*","read":true,"commands":["submit_expense"] }] }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("wildcard-entity grant may only grant commands ['*']"));
    }

    [Fact]
    public void Webhook_effect_must_be_https()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]![1]!["effects"]!).Add(JsonNode.Parse("""{ "type":"webhook","url":"http://insecure.example.com" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("webhook url must be https"));
    }

    [Theory]
    [InlineData("https://automation.internal.invalid/budget_planner/recalculate")]
    [InlineData("https://hooks.example.com/recalculate")]
    [InlineData("https://localhost/recalculate")]
    [InlineData("https://automation.test/run")]
    [InlineData("https://worker.internal/run")]
    public void Webhook_effect_may_not_point_at_a_host_that_cannot_exist(string url)
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]![1]!["effects"]!).Add(
            JsonNode.Parse($$"""{ "type":"webhook","url":"{{url}}" }"""));

        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("not a real service"));
    }

    [Fact]
    public void A_webhook_to_a_plausible_host_is_allowed_through()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["commands"]![1]!["effects"]!).Add(
            JsonNode.Parse("""{ "type":"webhook","url":"https://hooks.slack.com/services/T000/B000/xyz" }"""));

        Assert.DoesNotContain(Gate.SemanticErrors(doc), e => e.Contains("not a real service"));
    }

    [Fact]
    public void CreateRecord_effect_must_cover_required_target_fields()
    {
        var doc = ProcessDoc();
        ((JsonArray)doc["entities"]![0]!["fields"]!)[0]!["required"] = true;
        ((JsonArray)doc["commands"]![1]!["effects"]!).Add(JsonNode.Parse(
            """{ "type":"createRecord","entity":"expense","set":{"reason":"copy"} }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("does not set required field 'title'"));
    }

    private static JsonObject WithStatusField()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "stage", "label": "Stage", "type": "select", "role": "status", "options": [{ "value": "a", "label": "A" }] }"""));
        return doc;
    }

    [Fact]
    public void Kanban_without_any_groupable_field_is_rejected()
    {
        var doc = Minimal();
        doc["views"] = JsonNode.Parse(
            """[{ "key": "board", "label": "Board", "type": "kanban", "entity": "thing", "config": { "groupByField": "name" } }]""");
        var errs = Gate.SemanticErrors(doc);
        Assert.Contains(errs, e => e.Contains("(kanban) requires") && e.Contains("'select' or 'reference'"));
        Assert.Contains(errs, e => e.Contains("groupByField 'name' must be a select or reference field"));
    }

    [Fact]
    public void Kanban_with_a_status_field_is_valid()
    {
        var doc = WithStatusField();
        doc["views"] = JsonNode.Parse(
            """[{ "key": "board", "label": "Board", "type": "kanban", "entity": "thing", "config": { "groupByField": "stage" } }]""");
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Kanban_may_group_by_a_non_status_field_assignment_board()
    {
        var doc = WithStatusField();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "kind", "label": "Kind", "type": "select", "options": [{ "value": "x", "label": "X" }] }"""));
        doc["views"] = JsonNode.Parse(
            """[{ "key": "board", "label": "Board", "type": "kanban", "entity": "thing", "config": { "groupByField": "kind" } }]""");
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Kanban_grouped_by_a_reference_is_valid()
    {
        var doc = WithStatusField();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "station", "label": "Station", "type": "reference", "targetEntity": "station" }"""));
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse(
            """{ "key": "station", "label": "Station", "displayField": "name", "fields": [ { "key": "name", "label": "Name", "type": "text" } ] }"""));
        doc["views"] = JsonNode.Parse(
            """[{ "key": "board", "label": "Board", "type": "kanban", "entity": "thing", "config": { "groupByField": "station" } }]""");
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void Kanban_groupByField_must_have_discrete_columns()
    {
        var doc = WithStatusField();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "note", "label": "Note", "type": "text" }"""));
        doc["views"] = JsonNode.Parse(
            """[{ "key": "board", "label": "Board", "type": "kanban", "entity": "thing", "config": { "groupByField": "note" } }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("groupByField 'note' must be a select or reference field"));
    }

    [Fact]
    public void Calendar_without_a_date_field_is_rejected()
    {
        var doc = Minimal();
        doc["views"] = JsonNode.Parse(
            """[{ "key": "cal", "label": "Cal", "type": "calendar", "entity": "thing", "config": { "dateField": "name" } }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("(calendar) requires"));
    }

    private static JsonObject WithTableView()
    {
        var doc = Minimal();
        doc["views"] = JsonNode.Parse("""[{ "key": "things_table", "label": "Things", "type": "table", "entity": "thing" }]""");
        return doc;
    }

    [Fact]
    public void Authored_pages_with_valid_view_blocks_are_valid()
    {
        var doc = WithTableView();
        doc["pages"] = JsonNode.Parse("""
          [{ "key": "things_page", "label": "Things", "entity": "thing",
             "blocks": [{ "kind": "tabs", "tabs": [
                { "label": "All", "blocks": [{ "kind": "view", "view": "things_table" }] } ] }] }]
          """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Page_referencing_unknown_view_is_rejected()
    {
        var doc = WithTableView();
        doc["pages"] = JsonNode.Parse(
            """[{ "key": "p", "label": "P", "blocks": [{ "kind": "view", "view": "ghost_view" }] }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("page 'p' references unknown view 'ghost_view'"));
    }

    [Fact]
    public void Page_with_unknown_entity_is_rejected()
    {
        var doc = WithTableView();
        doc["pages"] = JsonNode.Parse(
            """[{ "key": "p", "label": "P", "entity": "ghost", "blocks": [{ "kind": "view", "view": "things_table" }] }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("page 'p' entity 'ghost' is unknown"));
    }

    [Fact]
    public void Page_columns_and_section_blocks_recurse_view_validation()
    {
        var doc = WithTableView();
        doc["pages"] = JsonNode.Parse("""
          [{ "key": "p", "label": "P", "blocks": [
             { "kind": "section", "label": "Left/Right", "blocks": [
               { "kind": "columns", "columns": [
                  [{ "kind": "view", "view": "things_table" }],
                  [{ "kind": "view", "view": "ghost" }] ] } ] }] }]
          """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("page 'p' references unknown view 'ghost'"));
    }

    private static JsonObject FormsApp() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "survey_app", "name": "Surveys", "version": "1.0.0",
      "plugins": [{ "id": "forms" }],
      "entities": [
        { "key": "survey", "label": "Survey", "role": "formTemplate", "displayField": "title",
          "fields": [{ "key": "title", "label": "Title", "type": "text" }] },
        { "key": "question", "label": "Question", "role": "formField", "displayField": "text",
          "fields": [
            { "key": "text", "label": "Text", "type": "text" },
            { "key": "survey", "label": "Survey", "type": "reference", "targetEntity": "survey" },
            { "key": "answer_type", "label": "Type", "type": "select", "role": "answerType",
              "options": [{ "value": "yes_no", "label": "Yes/No" }] },
            { "key": "position", "label": "Order", "type": "integer", "role": "order" }
          ] },
        { "key": "response", "label": "Response", "role": "formResponse",
          "fields": [{ "key": "survey", "label": "Survey", "type": "reference", "targetEntity": "survey" }] },
        { "key": "answer", "label": "Answer", "role": "formAnswer",
          "fields": [
            { "key": "response", "label": "Response", "type": "reference", "targetEntity": "response" },
            { "key": "question", "label": "Question", "type": "reference", "targetEntity": "question" },
            { "key": "value", "label": "Value", "type": "json", "role": "answerValue" }
          ] }
      ]
    }
    """)!;

    [Fact]
    public void Valid_forms_app_passes_the_gate() => Assert.Empty(Gate.Validate(FormsApp()));

    [Fact]
    public void Forms_app_without_a_formField_is_rejected()
    {
        var doc = FormsApp();
        ((JsonArray)doc["entities"]!)[1]!.AsObject().Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("no entity has role 'formField'"));
    }

    [Fact]
    public void Forms_app_without_a_formResponse_is_rejected()
    {
        var doc = FormsApp();
        ((JsonArray)doc["entities"]!)[2]!.AsObject().Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("no entity has role 'formResponse'"));
    }

    [Fact]
    public void Forms_app_without_a_formAnswer_is_rejected()
    {
        var doc = FormsApp();
        ((JsonArray)doc["entities"]!)[3]!.AsObject().Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("no entity has role 'formAnswer'"));
    }

    [Fact]
    public void FormField_without_an_answerType_field_is_rejected()
    {
        var doc = FormsApp();
        foreach (var f in (JsonArray)((JsonArray)doc["entities"]!)[1]!["fields"]!)
            if (f!["key"]!.GetValue<string>() == "answer_type") f.AsObject().Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("formField 'question' must have a field with role 'answerType'"));
    }

    [Fact]
    public void FormAnswer_without_an_answerValue_field_is_rejected()
    {
        var doc = FormsApp();
        foreach (var f in (JsonArray)((JsonArray)doc["entities"]!)[3]!["fields"]!)
            if (f!["key"]!.GetValue<string>() == "value") f.AsObject().Remove("role");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("formAnswer 'answer' must have a field with role 'answerValue'"));
    }

    private static JsonObject WithChild()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]!).Add(JsonNode.Parse("""
          { "key": "note", "label": "Note", "ownedBy": { "parent": "thing", "via": "thing_ref" },
            "fields": [
              { "key": "body", "label": "Body", "type": "longtext" },
              { "key": "thing_ref", "label": "Thing", "type": "reference", "targetEntity": "thing" } ] }
        """));
        return doc;
    }

    [Fact]
    public void OwnedBy_with_valid_parent_and_via_is_valid() => Assert.Empty(Gate.Validate(WithChild()));

    [Fact]
    public void OwnedBy_unknown_parent_is_rejected()
    {
        var doc = WithChild();
        ((JsonArray)doc["entities"]!)[1]!["ownedBy"]!["parent"] = "ghost";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("ownedBy.parent 'ghost' is unknown"));
    }

    [Fact]
    public void OwnedBy_via_not_a_reference_to_parent_is_rejected()
    {
        var doc = WithChild();
        ((JsonArray)doc["entities"]!)[1]!["ownedBy"]!["via"] = "body";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("ownedBy.via 'body' must be a reference to the parent 'thing'"));
    }

    [Fact]
    public void OwnedBy_itself_is_rejected()
    {
        var doc = WithChild();
        ((JsonArray)doc["entities"]!)[1]!["ownedBy"]!["parent"] = "note";
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("entity 'note' cannot be ownedBy itself"));
    }

    [Fact]
    public void Relation_from_a_platform_entity_is_valid()
    {
        var doc = (JsonObject)JsonNode.Parse("""
        {
          "schemaVersion": "1.0", "key": "leave", "name": "Leave", "version": "1.0.0",
          "entities": [
            { "key": "request", "label": "Request", "displayField": "reason", "fields": [
              { "key": "reason", "label": "Reason", "type": "text" },
              { "key": "requester", "label": "Requester", "type": "reference", "targetApp": "platform", "targetEntity": "person" } ] }
          ],
          "relations": [
            { "key": "requester_to_requests", "label": "Requests", "type": "oneToMany",
              "fromEntity": "person", "toEntity": "request", "inverseField": "requester" } ]
        }
        """)!;
        Assert.Empty(Gate.Validate(doc));
    }

    private static JsonObject Scheduling() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "sched", "name": "Sched", "version": "1.0.0",
      "entities": [
        { "key": "staff", "label": "Staff", "displayField": "name", "fields": [
          { "key": "name", "label": "Name", "type": "text" } ] },
        { "key": "shift", "label": "Shift", "displayField": "label", "fields": [
          { "key": "label", "label": "Label", "type": "text" },
          { "key": "shift_date", "label": "Date", "type": "date" },
          { "key": "start_time", "label": "Start", "type": "text" } ] },
        { "key": "assignment", "label": "Assignment", "displayField": "id", "fields": [
          { "key": "staff", "label": "Staff", "type": "reference", "targetEntity": "staff" },
          { "key": "shift", "label": "Shift", "type": "reference", "targetEntity": "shift" } ] }
      ]
    }
    """)!;

    private static JsonNode BoardPage() => JsonNode.Parse("""
    [{ "key": "board", "label": "Board", "blocks": [
      { "kind": "row", "gap": "none", "blocks": [
        { "kind": "stack", "width": "md", "blocks": [{ "kind": "text", "value": "Staff" }] },
        { "kind": "repeat", "as": "col", "direction": "row", "gap": "none",
          "source": { "dates": { "from": "{{today}}", "step": "day", "count": 7 } },
          "blocks": [{ "kind": "stack", "padding": "sm", "tone": "muted", "blocks": [
            { "kind": "text", "value": "{{col.label}}" }] }] }
      ]},
      { "kind": "repeat", "as": "row", "source": { "entity": "staff", "sort": [{ "field": "name", "direction": "asc" }] },
        "blocks": [
        { "kind": "row", "gap": "none", "blocks": [
          { "kind": "stack", "width": "md", "padding": "sm", "blocks": [{ "kind": "field", "field": "name" }] },
          { "kind": "repeat", "as": "cell", "direction": "row", "gap": "none",
            "source": { "dates": { "from": "{{today}}", "step": "day", "count": 7 } },
            "blocks": [
            { "kind": "stack", "padding": "xs", "bordered": true, "minHeight": 40, "blocks": [
              { "kind": "repeat",
                "source": { "entity": "assignment", "filters": [
                  { "field": "staff", "operator": "eq", "value": "{{row.id}}" },
                  { "path": "shift.shift_date", "operator": "eq", "value": "{{cell.date}}" } ] },
                "blocks": [{ "kind": "field", "field": "shift.start_time" }] } ] } ] }
        ]}]}
    ]}]
    """)!;

    private static JsonObject Matrix() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "cm", "name": "CM", "version": "1.0.0",
      "entities": [
        { "key": "competency", "label": "Competency", "displayField": "name", "fields": [
          { "key": "name", "label": "Name", "type": "text" } ] },
        { "key": "assessment", "label": "Assessment", "displayField": "id", "fields": [
          { "key": "person", "label": "Person", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
          { "key": "competency", "label": "Competency", "type": "reference", "targetEntity": "competency" },
          { "key": "level", "label": "Level", "type": "select",
            "options": [{ "value": "basic", "label": "Basic" }, { "value": "expert", "label": "Expert" }] } ] }
      ]
    }
    """)!;

    private static void Page(JsonObject doc, string cell) => doc["pages"] = JsonNode.Parse($$"""
    [{ "key": "review", "label": "Review", "blocks": [
      { "kind": "repeat", "as": "person", "source": { "platform": "person" }, "blocks": [
        { "kind": "repeat", "as": "comp", "source": { "entity": "competency" }, "blocks": [
          { "kind": "row", "blocks": [ {{cell}} ] } ] } ] } ] }]
    """);

    [Fact]
    public void An_editable_cell_keyed_on_both_axes_is_authorable()
    {
        var doc = Matrix();
        Page(doc, """
        { "kind":"cell","entity":"assessment","field":"level","editable":true,"placeholder":"—",
          "keys":{"person":"{{person.id}}","competency":"{{comp.id}}"} }
        """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void A_cell_key_must_be_a_field_of_the_cells_entity()
    {
        var doc = Matrix();
        Page(doc, """
        { "kind":"cell","entity":"assessment","field":"level",
          "keys":{"nope":"{{person.id}}"} }
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("cell key 'nope'"));
    }

    [Fact]
    public void A_cell_needs_keys_because_without_them_it_identifies_no_record()
    {
        var doc = Matrix();
        Page(doc, """{ "kind":"cell","entity":"assessment","field":"level" }""");
        Assert.Contains(Gate.Validate(doc), e => e.StartsWith("STRUCTURAL") && e.Contains("keys"));
    }

    [Fact]
    public void A_cell_cannot_show_the_value_that_identifies_it()
    {
        var doc = Matrix();
        Page(doc, """
        { "kind":"cell","entity":"assessment","field":"competency",
          "keys":{"person":"{{person.id}}","competency":"{{comp.id}}"} }
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("also one of its keys"));
    }

    [Fact]
    public void A_cell_field_must_exist_on_its_entity()
    {
        var doc = Matrix();
        Page(doc, """
        { "kind":"cell","entity":"assessment","field":"ghost","keys":{"person":"{{person.id}}"} }
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("cell field 'ghost'"));
    }

    [Fact]
    public void A_runtime_owned_base_field_cannot_be_made_editable()
    {
        var doc = Matrix();
        Page(doc, """
        { "kind":"cell","entity":"assessment","field":"created_at","editable":true,
          "keys":{"person":"{{person.id}}","competency":"{{comp.id}}"} }
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("base field") && e.Contains("created_at"));

        var ok = Matrix();
        Page(ok, """
        { "kind":"cell","entity":"assessment","field":"created_at",
          "keys":{"person":"{{person.id}}","competency":"{{comp.id}}"} }
        """);
        Assert.Empty(Gate.SemanticErrors(ok));
    }

    [Fact]
    public void Board_composed_from_primitives_is_authorable()
    {
        var doc = Scheduling();
        doc["pages"] = BoardPage();
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Repeat_over_a_date_axis_needs_no_entity()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "week", "label": "Week", "blocks": [
          { "kind": "repeat", "as": "col", "source": { "dates": { "from": "{{today}}", "step": "day", "count": 7 } },
            "blocks": [{ "kind": "text", "value": "{{col.label}}" }] }] }]
        """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Repeat_over_a_select_fields_options_is_an_axis()
    {
        var doc = Minimal();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "stage", "label": "Stage", "type": "select", "options": [{ "value": "new", "label": "New" }] }"""));
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "funnel", "label": "Funnel", "blocks": [
          { "kind": "repeat", "as": "col", "source": { "options": { "entity": "thing", "field": "stage" } },
            "blocks": [{ "kind": "text", "value": "{{col.label}}" }] }] }]
        """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Repeat_over_an_unknown_options_field_is_rejected()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "funnel", "label": "Funnel", "blocks": [
          { "kind": "repeat", "source": { "options": { "entity": "thing", "field": "nope" } },
            "blocks": [{ "kind": "text", "value": "x" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("options field 'nope'"));
    }

    [Fact]
    public void Repeat_over_a_non_select_options_field_is_rejected()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "funnel", "label": "Funnel", "blocks": [
          { "kind": "repeat", "source": { "options": { "entity": "thing", "field": "name" } },
            "blocks": [{ "kind": "text", "value": "x" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("must be a select"));
    }

    [Fact]
    public void Repeat_with_no_source_at_all_is_rejected()
    {
        var doc = Minimal();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "limit": 5 }, "blocks": [{ "kind": "text", "value": "x" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("needs one origin"));
    }

    [Fact]
    public void Path_filter_hopping_an_unknown_relation_is_rejected()
    {
        var doc = Scheduling();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "entity": "assignment", "filters": [
            { "path": "nope.shift_date", "operator": "eq", "value": "x" } ] },
            "blocks": [{ "kind": "text", "value": "x" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("hops 'nope'"));
    }

    [Fact]
    public void Path_filter_hopping_to_an_unknown_target_field_is_rejected()
    {
        var doc = Scheduling();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "entity": "assignment", "filters": [
            { "path": "shift.nope", "operator": "eq", "value": "x" } ] },
            "blocks": [{ "kind": "text", "value": "x" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'nope'") && e.Contains("shift"));
    }

    [Fact]
    public void Leaf_field_hopping_an_unknown_relation_is_rejected()
    {
        var doc = Scheduling();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "entity": "assignment" },
            "blocks": [{ "kind": "field", "field": "nope.start_time" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'nope'"));
    }

    [Fact]
    public void Leaf_field_hop_landing_on_a_reference_is_rejected()
    {
        var doc = Scheduling();
        doc["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "entity": "assignment" },
            "blocks": [{ "kind": "field", "field": "shift.label" }, { "kind": "field", "field": "staff.name" }] }] }]
        """);
        Assert.Empty(Gate.SemanticErrors(doc));

        var bad = Scheduling();
        ((JsonArray)bad["entities"]![1]!["fields"]!).Add(JsonNode.Parse(
            """{ "key": "store", "label": "Store", "type": "reference", "targetEntity": "staff" }"""));
        bad["pages"] = JsonNode.Parse("""
        [{ "key": "p", "label": "P", "blocks": [
          { "kind": "repeat", "source": { "entity": "assignment" },
            "blocks": [{ "kind": "field", "field": "shift.store" }] }] }]
        """);
        Assert.Contains(Gate.SemanticErrors(bad), e => e.Contains("shift.store") && e.Contains("reference"));
    }

    private static JsonObject WithInitial(string initialJson) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "rb", "name": "RB", "version": "1.0.0",
      "entities": [
        { "key": "room", "label": "Room", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "tier", "label": "Tier", "type": "select",
              "options": [ { "value": "standard", "label": "Standard" }, { "value": "premium", "label": "Premium" } ] }
          ] },
        { "key": "booking", "label": "Booking", "displayField": "purpose",
          "fields": [
            { "key": "purpose", "label": "Purpose", "type": "text" },
            { "key": "room", "label": "Room", "type": "reference", "targetEntity": "room" },
            { "key": "status", "label": "Status", "type": "select", "default": "pending",
              "options": [ { "value": "pending", "label": "Pending" }, { "value": "approved", "label": "Approved" } ],
              "initial": {{initialJson}} }
          ] }
      ]
    }
    """)!;

    [Fact]
    public void Initial_rule_with_a_valid_relation_hop_is_accepted() =>
        Assert.Empty(Gate.Validate(WithInitial(
            """[ { "when": { "path": "room.tier", "operator": "eq", "value": "standard" }, "value": "approved" } ]""")));

    [Fact]
    public void Initial_rule_with_an_unknown_hop_target_is_rejected() =>
        Assert.Contains(
            Gate.SemanticErrors(WithInitial(
                """[ { "when": { "path": "room.nonesuch", "operator": "eq", "value": "x" }, "value": "approved" } ]""")),
            e => e.Contains("initial") && e.Contains("room.nonesuch"));

    [Fact]
    public void Initial_rule_hopping_through_a_non_reference_field_is_rejected() =>
        Assert.Contains(
            Gate.SemanticErrors(WithInitial(
                """[ { "when": { "path": "purpose.x", "operator": "eq", "value": "y" }, "value": "approved" } ]""")),
            e => e.Contains("not a reference field"));

    [Fact]
    public void Initial_rule_value_must_be_a_valid_option_of_its_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithInitial(
                """[ { "when": { "path": "room.tier", "operator": "eq", "value": "standard" }, "value": "confirmed" } ]""")),
            e => e.Contains("not an option"));

    [Fact]
    public void Time_off_reference_is_fully_valid() =>
        Assert.Empty(Gate.Validate(Fixture("time-off.appdef.json")));

    private static JsonObject WithTimeline(string timelineJson) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "t", "name": "T", "version": "1.0.0",
      "entities": [
        { "key": "leave", "label": "Leave", "displayField": "kind",
          "fields": [
            { "key": "who", "label": "Who", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "kind", "label": "Kind", "type": "select", "options": [ { "value": "a", "label": "A" } ] },
            { "key": "d1", "label": "From", "type": "date" },
            { "key": "d2", "label": "To", "type": "date" }
          ] }
      ],
      "pages": [ { "key": "p", "label": "P", "blocks": [ {{timelineJson}} ] } ]
    }
    """)!;

    [Fact]
    public void Timeline_over_a_reference_lane_and_date_fields_is_accepted() =>
        Assert.Empty(Gate.Validate(WithTimeline(
            """{ "kind": "timeline", "entity": "leave", "rowBy": "who", "startField": "d1", "endField": "d2", "axis": { "from": "{{today}}", "count": 14 }, "colorField": "kind" }""")));

    [Fact]
    public void Timeline_rowBy_must_be_a_reference_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithTimeline(
                """{ "kind": "timeline", "entity": "leave", "rowBy": "kind", "startField": "d1", "axis": { "from": "{{today}}", "count": 14 } }""")),
            e => e.Contains("rowBy") && e.Contains("reference field"));

    [Fact]
    public void Timeline_startField_must_be_a_date_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithTimeline(
                """{ "kind": "timeline", "entity": "leave", "rowBy": "who", "startField": "who", "axis": { "from": "{{today}}", "count": 14 } }""")),
            e => e.Contains("startField") && e.Contains("date field"));

    [Fact]
    public void Task_manager_reference_is_fully_valid() =>
        Assert.Empty(Gate.Validate(Fixture("task-manager.appdef.json")));

    private static JsonObject WithNavSource(string navJson)
    {
        var doc = Minimal();
        doc["schemaVersion"] = "2.0";
        doc["pages"] = JsonNode.Parse($$"""
        [ { "key": "things", "label": "Things", "entity": "thing", "navSource": {{navJson}},
            "blocks": [ { "kind": "text", "text": "x" } ] } ]
        """);
        return doc;
    }

    [Fact]
    public void NavSource_over_a_real_label_field_is_accepted() =>
        Assert.Empty(Gate.Validate(WithNavSource("""{ "labelField": "name" }""")));

    [Fact]
    public void NavSource_labelField_must_be_a_field_of_the_page_entity() =>
        Assert.Contains(
            Gate.SemanticErrors(WithNavSource("""{ "labelField": "nonesuch" }""")),
            e => e.Contains("navSource") && e.Contains("nonesuch"));

    private static JsonObject WithComputed(string invoiceExtra = "", string lineExtra = "") => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "inv", "name": "Inv", "version": "1.0.0",
      "entities": [
        { "key": "invoice", "label": "Invoice", "displayField": "number",
          "fields": [
            { "key": "number", "label": "Number", "type": "text" },
            { "key": "tax_rate", "label": "Tax %", "type": "decimal" }{{invoiceExtra}}
          ] },
        { "key": "line", "label": "Line", "displayField": "description",
          "ownedBy": { "parent": "invoice", "via": "invoice" },
          "fields": [
            { "key": "description", "label": "Description", "type": "text" },
            { "key": "invoice", "label": "Invoice", "type": "reference", "targetEntity": "invoice" },
            { "key": "quantity", "label": "Qty", "type": "decimal" },
            { "key": "unit_price", "label": "Unit price", "type": "money" },
            { "key": "kind", "label": "Kind", "type": "select",
              "options": [ { "value": "service", "label": "Service" }, { "value": "expense", "label": "Expense" } ] }{{lineExtra}}
          ] }
      ]
    }
    """)!;

    private const string LineTotal = """
        , { "key": "line_total", "label": "Line total", "type": "money", "computed": { "expr": "quantity * unit_price" } }
        """;

    [Fact]
    public void Computed_expr_and_rollup_document_math_is_accepted() =>
        Assert.Empty(Gate.Validate(WithComputed(
            invoiceExtra: """
                , { "key": "subtotal", "label": "Subtotal", "type": "money",
                    "computed": { "rollup": { "entity": "line", "via": "invoice", "op": "sum", "field": "line_total",
                      "filters": [ { "field": "kind", "operator": "eq", "value": "service" } ] } } }
                , { "key": "total", "label": "Total", "type": "money", "computed": { "expr": "subtotal * (1 + tax_rate / 100)" } }
                """,
            lineExtra: LineTotal)));

    [Fact]
    public void Computed_needs_exactly_one_of_expr_or_rollup() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money", "computed": { } }
                """)),
            e => e.Contains("exactly one of 'expr' or 'rollup'"));

    [Fact]
    public void Computed_is_rejected_on_a_text_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "text", "computed": { "expr": "tax_rate" } }
                """)),
            e => e.Contains("only valid on integer/decimal/money/boolean"));

    [Fact]
    public void Computed_cannot_combine_with_default() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money", "default": 5, "computed": { "expr": "tax_rate" } }
                """)),
            e => e.Contains("cannot combine with 'default'"));

    [Fact]
    public void Expr_referencing_an_unknown_field_is_rejected() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money", "computed": { "expr": "nonesuch * 2" } }
                """)),
            e => e.Contains("'nonesuch' is not a field"));

    [Fact]
    public void Expr_referencing_a_non_numeric_field_is_rejected() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money", "computed": { "expr": "number * 2" } }
                """)),
            e => e.Contains("'number' is not a numeric, boolean, or date field"));

    [Fact]
    public void Expr_referencing_another_expr_field_is_accepted() =>
        Assert.Empty(
            Gate.Validate(WithComputed(lineExtra: LineTotal + """
                , { "key": "double_total", "label": "2x", "type": "money", "computed": { "expr": "line_total * 2" } }
                """)));

    [Fact]
    public void Numeric_comparison_can_compute_a_boolean() =>
        Assert.Empty(Gate.Validate(WithComputed(lineExtra: """
            , { "key": "available", "label": "Available", "type": "decimal", "computed": { "expr": "quantity - unit_price" } }
            , { "key": "below_minimum", "label": "Below minimum", "type": "boolean", "computed": { "expr": "available < quantity" } }
            """)));

    [Fact]
    public void Date_comparison_can_compute_a_boolean() =>
        Assert.Empty(Gate.Validate(WithComputed(invoiceExtra: """
            , { "key": "needed_by", "label": "Needed by", "type": "date" }
            , { "key": "actual_arrival", "label": "Actual arrival", "type": "date" }
            , { "key": "arrived_on_time", "label": "Arrived on time", "type": "boolean",
                "computed": { "expr": "actual_arrival <= needed_by" } }
            """)));

    [Fact]
    public void Computed_expression_cycles_are_rejected() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(lineExtra: """
                , { "key": "a", "label": "A", "type": "decimal", "computed": { "expr": "b + 1" } }
                , { "key": "b", "label": "B", "type": "decimal", "computed": { "expr": "a + 1" } }
                """)),
            error => error.Contains("computed expression cycle"));

    [Fact]
    public void Expression_result_must_match_its_field_type() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(lineExtra: """
                , { "key": "bad", "label": "Bad", "type": "boolean", "computed": { "expr": "quantity * 2" } }
                """)),
            error => error.Contains("returns a number, not a boolean"));

    [Fact]
    public void Rollup_via_must_point_back_at_the_declaring_entity() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money",
                    "computed": { "rollup": { "entity": "line", "via": "kind", "op": "sum", "field": "unit_price" } } }
                """)),
            e => e.Contains("must be a local reference field"));

    [Fact]
    public void Rollup_sum_needs_a_numeric_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "money",
                    "computed": { "rollup": { "entity": "line", "via": "invoice", "op": "sum" } } }
                """)),
            e => e.Contains("needs a 'field' to aggregate"));

    [Fact]
    public void Rollup_count_must_not_name_a_field() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "integer",
                    "computed": { "rollup": { "entity": "line", "via": "invoice", "op": "count", "field": "quantity" } } }
                """)),
            e => e.Contains("must not name a 'field'"));

    private const string InvoiceDates = """
        , { "key": "issued_on", "label": "Issued on", "type": "date" }
        , { "key": "sent_at", "label": "Sent at", "type": "datetime" }
        """;

    [Fact]
    public void Date_parts_over_a_date_field_are_accepted() =>
        Assert.Empty(Gate.Validate(WithComputed(invoiceExtra: InvoiceDates + """
            , { "key": "wd", "label": "Weekday", "type": "integer", "computed": { "expr": "weekday(issued_on)" } }
            , { "key": "wk", "label": "Week", "type": "integer", "computed": { "expr": "week_of_year(issued_on)" } }
            , { "key": "mo", "label": "Month", "type": "integer", "computed": { "expr": "month_of(issued_on)" } }
            , { "key": "dm", "label": "Day", "type": "integer", "computed": { "expr": "day_of_month(issued_on)" } }
            , { "key": "dy", "label": "Day of year", "type": "integer", "computed": { "expr": "day_of_year(issued_on)" } }
            , { "key": "yr", "label": "Year", "type": "integer", "computed": { "expr": "year_of(issued_on)" } }
            """)));

    [Fact]
    public void An_hour_needs_a_datetime() =>
        Assert.Empty(Gate.Validate(WithComputed(invoiceExtra: InvoiceDates + """
            , { "key": "hr", "label": "Hour", "type": "integer", "computed": { "expr": "hour_of(sent_at)" } }
            """)));

    [Fact]
    public void An_hour_of_a_plain_date_is_refused() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "hr", "label": "Hour", "type": "integer", "computed": { "expr": "hour_of(issued_on)" } }
                """)),
            e => e.Contains("has no time of day"));

    [Fact]
    public void A_date_part_of_something_that_is_not_a_date_is_refused() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "wd", "label": "Weekday", "type": "integer", "computed": { "expr": "weekday(tax_rate)" } }
                """)),
            e => e.Contains("must be a date/datetime field"));

    [Fact]
    public void A_date_part_takes_one_field_and_says_so() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "wd", "label": "Weekday", "type": "integer",
                    "computed": { "expr": "weekday(issued_on, sent_at)" } }
                """)),
            e => e.Contains("takes exactly one date field"));

    [Fact]
    public void A_date_part_that_returns_a_date_is_still_unknown() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "sw", "label": "Week start", "type": "integer",
                    "computed": { "expr": "start_of_week(issued_on)" } }
                """)),
            e => e.Contains("is not a known function"));

    [Fact]
    public void The_clock_is_not_a_function() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "age", "label": "Age", "type": "integer",
                    "computed": { "expr": "days_between(issued_on, today())" } }
                """)),
            e => e.Contains("is not available in a computed field"));

    [Fact]
    public void The_clock_refusal_says_why_rather_than_calling_it_a_typo() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: InvoiceDates + """
                , { "key": "age", "label": "Age", "type": "integer",
                    "computed": { "expr": "days_between(issued_on, now)" } }
                """)),
            e => e.Contains("stop being true the next day"));

    [Fact]
    public void Rollup_filter_fields_must_exist_on_the_aggregated_entity() =>
        Assert.Contains(
            Gate.SemanticErrors(WithComputed(invoiceExtra: """
                , { "key": "x", "label": "X", "type": "integer",
                    "computed": { "rollup": { "entity": "line", "via": "invoice", "op": "count",
                      "filters": [ { "field": "nonesuch", "operator": "eq", "value": "a" } ] } } }
                """)),
            e => e.Contains("rollup filter field 'nonesuch'"));

    private static JsonObject HubDoc(string actions, string placements = """["recordHeader"]""") =>
        (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0",
          "entities":[
            { "key":"org","label":"Org","displayField":"name",
              "fields":[{"key":"name","label":"Name","type":"text"}],
              "detail":{"blocks":[{"kind":"hub","title":"name","actions":{{actions}}}]} }
          ],
          "commands":[
            {"key":"research","label":"Research now","entity":"org","placements":{{placements}},
             "effects":[{"type":"notify","message":"queued"}]}
          ]
        }
        """)!;

    [Fact]
    public void A_recordHeader_command_missing_from_the_hub_actions_is_rejected() =>
        Assert.Contains(
            Gate.Validate(HubDoc("""["edit","delete"]""")),
            e => e.Contains("'research'") && e.Contains("recordHeader") && e.Contains("hub actions"));

    [Fact]
    public void A_recordHeader_command_listed_in_the_hub_actions_is_accepted() =>
        Assert.Empty(Gate.Validate(HubDoc("""["research","edit","delete"]""")));

    [Fact]
    public void A_command_that_asks_for_no_header_placement_needs_no_hub_action() =>
        Assert.Empty(Gate.Validate(HubDoc("""["edit","delete"]""", """["tableRow"]""")));

    [Fact]
    public void A_transition_bound_command_is_exempt_from_the_hub_actions_rule()
    {
        var doc = ProcessDoc();
        ((JsonObject)doc["entities"]![0]!)["detail"] = JsonNode.Parse(
            """{"blocks":[{"kind":"hub","title":"title","actions":["submit_expense","edit","delete"]}]}""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void An_entity_with_no_hub_is_not_held_to_the_rule() =>
        Assert.Empty(Gate.Validate(WithSelfTargetCommand()));
}
