// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class PickGateTests
{
    private static JsonObject Rota(string pickJson) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "rota", "name": "Rota", "version": "1.0.0",
      "entities": [
        { "key": "member", "label": "Member", "displayField": "name", "fields": [
          { "key": "name", "label": "Name", "type": "text" },
          { "key": "last_brought", "label": "Last brought", "type": "date" } ] },
        { "key": "week", "label": "Week", "displayField": "label", "fields": [
          { "key": "label", "label": "Label", "type": "text" },
          { "key": "person", "label": "Person", "type": "reference", "targetEntity": "member" } ] }
      ],
      "pages": [
        { "key": "home", "label": "Weeks", "entity": "week", "blocks": [
          { "kind": "view", "view": "weeks" },
          { "kind": "create", "entity": "week", "label": "New week" } ] },
        { "key": "people", "label": "People", "entity": "member", "group": "config", "blocks": [
          { "kind": "view", "view": "members" },
          { "kind": "create", "entity": "member", "label": "Add member" } ] }
      ],
      "views": [
        { "key": "weeks", "label": "Weeks", "type": "table", "entity": "week",
          "config": { "columns": ["label", "person"] } },
        { "key": "members", "label": "Members", "type": "table", "entity": "member",
          "config": { "columns": ["name", "last_brought"] } }
      ],
      "workflows": [
        { "key": "next_turn", "name": "Next turn",
          "trigger": { "event": "record.created", "entity": "week" },
          "effects": [ { "type": "updateRecord", "set": { "person": { "pick": {{pickJson}} } } } ] }
      ]
    }
    """)!;

    private static IReadOnlyList<string> Errors(string pickJson) => Gate.Validate(Rota(pickJson));

    [Fact]
    public void A_well_formed_pick_is_accepted() =>
        Assert.Empty(Errors("""
        { "entity": "member", "sort": [{ "field": "last_brought", "direction": "asc" }] }
        """));

    [Fact]
    public void A_pick_on_an_unknown_entity_is_refused() =>
        Assert.Contains(Errors("""
        { "entity": "people", "sort": [{ "field": "last_brought" }] }
        """), e => e.Contains("pick reads unknown entity 'people'"));

    [Fact]
    public void A_pick_sorted_by_a_field_that_does_not_exist_is_refused() =>
        Assert.Contains(Errors("""
        { "entity": "member", "sort": [{ "field": "last_coffee" }] }
        """), e => e.Contains("pick sorts by 'last_coffee'"));

    [Fact]
    public void A_pick_with_no_sort_is_refused() =>
        Assert.Contains(Errors("""
        { "entity": "member" }
        """), e => e.Contains("has no sort"));

    [Fact]
    public void A_pick_filtered_on_an_unknown_field_is_refused() =>
        Assert.Contains(Errors("""
        { "entity": "member", "filters": [{ "field": "on_holiday", "operator": "eq", "value": false }],
          "sort": [{ "field": "last_brought" }] }
        """), e => e.Contains("pick filter field 'on_holiday'"));

    [Fact]
    public void A_pick_reading_a_field_the_entity_does_not_have_is_refused() =>
        Assert.Contains(Errors("""
        { "entity": "member", "field": "nickname", "sort": [{ "field": "last_brought" }] }
        """), e => e.Contains("pick reads 'nickname'"));
}
