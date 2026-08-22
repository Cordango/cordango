// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class ExpressionPlaneGateTests
{
    private static JsonObject Bookings() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "book", "name": "Bookings", "version": "1.0.0",
      "entities": [
        { "key": "room", "label": "Room", "displayField": "name", "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "requires_approval", "label": "Needs approval", "type": "boolean" } ] },
        { "key": "booking", "label": "Booking", "displayField": "title", "fields": [
            { "key": "title", "label": "Title", "type": "text" },
            { "key": "starts", "label": "Starts", "type": "datetime" },
            { "key": "ends", "label": "Ends", "type": "datetime" },
            { "key": "owner", "label": "Owner", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "room", "label": "Room", "type": "reference", "targetEntity": "room" },
            { "key": "state", "label": "State", "type": "select", "role": "status", "options": [
                { "value": "draft", "label": "Draft" }, { "value": "approved", "label": "Approved" } ] } ] }
      ]
    }
    """)!;

    private static JsonObject WithCommandWhen(string whenJson)
    {
        var doc = Bookings();
        doc["commands"] = JsonNode.Parse($$"""
        [ { "key": "approve", "label": "Approve", "entity": "booking", "when": {{whenJson}},
            "effects": [ { "type": "updateRecord", "target": "self", "set": { "state": "approved" } } ] } ]
        """);
        return doc;
    }

    private static List<string> Errors(string whenJson) => Gate.Validate(WithCommandWhen(whenJson));

    [Theory]
    [InlineData("\"{{today}}\"")]
    [InlineData("\"{{now}}\"")]
    [InlineData("\"{{today+7}}\"")]
    [InlineData("\"{{today-30d}}\"")]
    [InlineData("\"{{today+2w}}\"")]
    [InlineData("\"{{now-4h}}\"")]
    [InlineData("\"{{actor.id}}\"")]
    [InlineData("\"{{currentUser.id}}\"")]
    public void Every_token_the_evaluator_resolves_passes_the_gate(string value) =>
        Assert.Empty(Errors($$"""{ "field": "starts", "operator": "gte", "value": {{value}} }"""));

    [Fact]
    public void A_month_offset_is_rejected_because_the_two_runtimes_would_disagree()
    {
        var errs = Errors("""{ "field": "starts", "operator": "gte", "value": "{{today+1m}}" }""");
        Assert.Contains(errs, e => e.Contains("token") && e.Contains("today+1m"));
    }

    [Fact]
    public void An_hour_offset_on_a_date_anchor_is_rejected_with_the_fix()
    {
        var errs = Errors("""{ "field": "starts", "operator": "gte", "value": "{{today-4h}}" }""");
        Assert.Contains(errs, e => e.Contains("{{now}}") && e.Contains("hours"));
    }

    [Fact]
    public void An_invented_token_is_still_rejected()
    {
        var errs = Errors("""{ "field": "starts", "operator": "gte", "value": "{{yesterday}}" }""");
        Assert.Contains(errs, e => e.Contains("yesterday"));
    }

    [Fact]
    public void Tokens_inside_a_range_value_are_checked_element_by_element()
    {
        Assert.Empty(Errors("""{ "field": "starts", "operator": "between", "value": ["{{today}}", "{{today+7}}"] }"""));
        var errs = Errors("""{ "field": "starts", "operator": "between", "value": ["{{today}}", "{{next_week}}"] }""");
        Assert.Contains(errs, e => e.Contains("next_week"));
    }

    [Fact]
    public void Between_needs_a_pair()
    {
        var errs = Errors("""{ "field": "starts", "operator": "between", "value": "{{today}}" }""");
        Assert.Empty(errs);
        errs = Errors("""{ "field": "starts", "operator": "between", "value": ["{{today}}"] }""");
        Assert.Contains(errs, e => e.Contains("two-element") && e.Contains("[lo, hi]"));
    }

    [Fact]
    public void Overlaps_needs_the_end_field_that_completes_the_range()
    {
        var errs = Errors("""{ "field": "starts", "operator": "overlaps", "value": ["{{today}}", "{{today+7}}"] }""");
        Assert.Contains(errs, e => e.Contains("'endField'") && e.Contains("[field, endField]"));

        Assert.Empty(Errors("""
        { "field": "starts", "endField": "ends", "operator": "overlaps", "value": ["{{today}}", "{{today+7}}"] }
        """));
    }

    [Fact]
    public void An_end_field_must_resolve_and_only_means_something_for_overlaps()
    {
        var errs = Errors("""
        { "field": "starts", "endField": "finishes", "operator": "overlaps", "value": ["{{today}}", "{{today+7}}"] }
        """);
        Assert.Contains(errs, e => e.Contains("endField 'finishes' is not a field"));

        errs = Errors("""{ "field": "starts", "endField": "ends", "operator": "gte", "value": "{{today}}" }""");
        Assert.Contains(errs, e => e.Contains("only means something with operator 'overlaps'"));
    }

    [Fact]
    public void A_condition_may_hop_one_same_app_reference()
    {
        Assert.Empty(Errors("""{ "path": "room.requires_approval", "operator": "eq", "value": true }"""));
        Assert.Empty(Errors("""
        { "all": [ { "field": "state", "operator": "eq", "value": "draft" },
                   { "path": "room.requires_approval", "operator": "eq", "value": true } ] }
        """));
    }

    [Fact]
    public void A_hop_the_engine_cannot_follow_is_rejected_at_author_time()
    {
        Assert.Contains(Errors("""{ "path": "owner.email", "operator": "eq", "value": "x" }"""),
            e => e.Contains("another app's data"));
        Assert.Contains(Errors("""{ "path": "title.name", "operator": "eq", "value": "x" }"""),
            e => e.Contains("only a reference can be hopped through"));
        Assert.Contains(Errors("""{ "path": "room.colour", "operator": "eq", "value": "x" }"""),
            e => e.Contains("'colour', which is not a field of 'room'"));
        Assert.Contains(Errors("""{ "path": "roomonly", "operator": "eq", "value": "x" }"""),
            e => e.Contains("<reference field>.<field on the target>"));
    }

    [Fact]
    public void A_leaf_must_address_its_value_exactly_once()
    {
        Assert.Contains(Errors("""{ "field": "state", "path": "room.requires_approval", "operator": "eq", "value": true }"""),
            e => e.StartsWith("STRUCTURAL") && e.Contains("/commands/0/when"));
        Assert.Contains(Errors("""{ "operator": "eq", "value": true }"""),
            e => e.StartsWith("STRUCTURAL") && e.Contains("/commands/0/when"));
    }

    [Fact]
    public void The_baseline_app_is_valid_so_the_negatives_above_mean_something() =>
        Assert.Empty(Gate.Validate(Bookings()));

    private static JsonObject WithBlocks(string? detailBlocks, string pageBlocks)
    {
        var doc = Bookings();
        doc["views"] = JsonNode.Parse("""[ { "key": "b_table", "label": "Bookings", "type": "table", "entity": "booking" } ]""");
        if (detailBlocks is not null)
            doc["entities"]!.AsArray().OfType<JsonObject>().First(e => (string?)e["key"] == "booking")["detail"] =
                JsonNode.Parse($$"""{ "blocks": {{detailBlocks}} }""");
        doc["pages"] = JsonNode.Parse($$"""
        [ { "key": "b", "label": "Bookings", "icon": "calendar", "entity": "booking", "blocks": {{pageBlocks}} } ]
        """);
        return doc;
    }

    [Fact]
    public void The_history_block_needs_no_authoring_beyond_being_placed()
    {
        Assert.Empty(Gate.Validate(WithBlocks(
            """[ { "kind": "hub" }, { "kind": "history", "label": "Activity", "limit": 20 } ]""",
            """[ { "kind": "view", "view": "b_table" } ]""")));
    }

    [Fact]
    public void A_history_block_outside_a_record_detail_is_rejected()
    {
        Assert.Contains(
            Gate.Validate(WithBlocks(null, """[ { "kind": "history" } ]""")),
            e => e.Contains("'history' is only valid in a record detail"));
    }
}
