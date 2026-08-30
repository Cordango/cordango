// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// An `action` that names its own record by keys, rather than taking one from the surface it sits on.
///
/// <para>The shape a punch clock needs: the first click of the day has no booking to bind to, and a
/// button that only works once somebody has opened a form first is not the button.</para>
/// </summary>
public class SelfAnchoredActionGateTests
{
    private static JsonObject Clock() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "tc", "name": "Time clock", "version": "1.0.0",
      "entities": [
        { "key": "workday", "label": "Workday", "displayField": "day", "fields": [
          { "key": "day", "label": "Day", "type": "date" },
          { "key": "worker", "label": "Worker", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
          { "key": "note", "label": "Note", "type": "text" } ] },
        { "key": "policy", "label": "Policy", "kind": "settings", "displayField": "rounding", "fields": [
          { "key": "rounding", "label": "Rounding", "type": "integer" } ] }
      ],
      "commands": [
        { "key": "punch", "label": "Stempeln", "entity": "workday",
          "effects": [ { "type": "updateRecord", "set": { "note": "punched" } } ] }
      ]
    }
    """)!;

    private static void Page(JsonObject doc, string action) => doc["pages"] = JsonNode.Parse(
        "[{ \"key\": \"clock\", \"label\": \"Stempeluhr\", \"blocks\": [ " + action + " ] }]");

    private const string Keys = "\"keys\":{\"day\":\"{{today}}\",\"worker\":\"{{actor.id}}\"}";

    [Fact]
    public void A_page_button_may_name_its_own_record_by_keys()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\",\"entity\":\"workday\"," + Keys + " }");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void A_key_must_be_a_field_of_the_actions_entity()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\",\"entity\":\"workday\",\"keys\":{\"nope\":\"x\"} }");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("action key 'nope'"));
    }

    [Fact]
    public void The_command_must_belong_to_the_entity_the_action_names()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"nosuch\",\"entity\":\"workday\"," + Keys + " }");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'nosuch' is not a command on 'workday'"));
    }

    [Fact]
    public void An_unknown_entity_is_named_rather_than_read_as_a_missing_binding()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\",\"entity\":\"ghost\"," + Keys + " }");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("action entity 'ghost' is unknown"));
    }

    [Fact]
    public void A_singleton_cannot_be_identified_by_keys_because_there_is_only_one()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\",\"entity\":\"policy\",\"keys\":{\"rounding\":\"1\"} }");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("only ever one of it"));
    }

    [Fact]
    public void Keys_without_an_entity_are_refused_rather_than_ignored()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\"," + Keys + " }");
        Assert.NotEmpty(Gate.Validate(doc));
    }

    [Fact]
    public void A_bound_action_on_a_page_with_no_record_names_the_way_out()
    {
        var doc = Clock();
        Page(doc, "{ \"kind\":\"action\",\"command\":\"punch\" }");
        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("requires a record") && e.Contains("'entity' + 'keys'"));
    }
}
