// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;

namespace Cordango.Compiler.Tests;

public class FormFieldOwnershipTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Doc() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "leave", "name": "Leave", "version": "1.0.0",
      "entities": [
        { "key": "request", "label": "Request", "labelPlural": "Requests", "displayField": "reason",
          "fields": [
            { "key": "reason", "label": "Reason", "type": "text", "required": true },
            { "key": "amount", "label": "Amount", "type": "money", "currency": "EUR" },
            { "key": "requested_by", "label": "Person", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "approver", "label": "Approver", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "confirmed_by", "label": "Confirmed by", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "submitted_at", "label": "Submitted", "type": "datetime" },
            { "key": "decided_at", "label": "Decided", "type": "datetime" },
            { "key": "tier", "label": "Tier", "type": "select",
              "options": [ { "value": "low", "label": "Low" }, { "value": "high", "label": "High" } ],
              "initial": [ { "when": { "field": "amount", "operator": "lt", "value": 500 }, "value": "low" },
                           { "when": { "field": "amount", "operator": "gte", "value": 500 }, "value": "high" } ] },
            { "key": "parent", "label": "Parent", "type": "reference", "targetEntity": "request" },
            { "key": "parent_note", "label": "Parent note", "type": "text" },
            { "key": "status", "label": "Status", "type": "select", "role": "status" }
          ] },
        { "key": "nomination", "label": "Nomination", "labelPlural": "Nominations", "displayField": "why",
          "fields": [
            { "key": "why", "label": "Why", "type": "text" },
            { "key": "reviewer", "label": "Reviewer", "type": "reference", "required": true, "targetApp": "platform", "targetEntity": "person" },
            { "key": "decision", "label": "Decision", "type": "select", "required": true, "default": "open",
              "options": [ { "value": "open", "label": "Open" }, { "value": "agreed", "label": "Agreed" } ] }
          ] }
      ],
      "processes": [
        { "key": "flow", "entity": "request", "stateField": "status", "initialState": "draft",
          "states": [ { "key": "draft", "label": "Draft" }, { "key": "pending", "label": "Pending" },
                      { "key": "approved", "label": "Approved", "terminal": true } ],
          "transitions": [
            { "key": "submit", "label": "Submit", "from": ["draft"], "to": "pending", "command": "submit" },
            { "key": "approve", "label": "Approve", "from": ["pending"], "to": "approved", "command": "approve" }
          ] }
      ],
      "commands": [
        { "key": "submit", "label": "Submit", "entity": "request",
          "effects": [ { "type": "updateRecord", "set": { "submitted_at": "{{now}}", "reason": "{{record.reason}}" } } ] },
        { "key": "approve", "label": "Approve", "entity": "request",
          "effects": [
            { "type": "updateRecord", "set": { "confirmed_by": "{{actor.id}}", "decided_at": "{{now}}" } },
            { "type": "updateRecord", "target": { "field": "parent" }, "set": { "parent_note": "child approved" } }
          ] }
      ],
      "views": [ { "key": "all", "label": "All", "type": "table", "entity": "request" } ],
      "roles": [ { "key": "admin", "name": "Admin",
                   "grants": [ { "entity": "*", "create": true, "read": true, "update": true, "delete": true } ] } ]
    }
    """)!;

    private static JsonObject Field(string entity, string key, Action<JsonObject>? mutate = null)
    {
        var doc = Doc();
        mutate?.Invoke(doc);
        var manifest = AppCompiler.Compile(doc, "app", At);
        return ((JsonArray)manifest["entities"]!).OfType<JsonObject>().First(e => (string?)e["key"] == entity)["fields"]!
            .AsArray().OfType<JsonObject>().First(f => (string?)f["key"] == key);
    }

    private static bool Flag(JsonObject f, string name) => f[name]?.GetValue<bool>() == true;

    [Fact]
    public void An_initial_ruled_field_is_not_asked_for_on_create()
    {
        var tier = Field("request", "tier");

        Assert.True(Flag(tier, "hideOnCreate"));
        Assert.False(Flag(tier, "readOnly"));
    }

    [Fact]
    public void A_required_reviewer_with_nothing_else_to_fill_it_stays_on_the_create_form()
    {
        var reviewer = Field("nomination", "reviewer");

        Assert.False(Flag(reviewer, "hideOnCreate"));
    }

    [Fact]
    public void A_required_decision_with_a_default_is_still_hidden_on_create() =>
        Assert.True(Flag(Field("nomination", "decision"), "hideOnCreate"));

    [Fact]
    public void An_optional_approver_is_still_hidden_on_create() =>
        Assert.True(Flag(Field("request", "approver"), "hideOnCreate"));

    [Fact]
    public void A_field_a_command_writes_belongs_to_the_command()
    {
        var decided = Field("request", "decided_at");

        Assert.True(Flag(decided, "setByCommand"));
    }

    [Fact]
    public void A_written_field_loses_the_creator_stamp_the_name_suggested()
    {
        var confirmed = Field("request", "confirmed_by");
        var submitted = Field("request", "submitted_at");

        Assert.True(Flag(confirmed, "setByCommand"));
        Assert.Null(confirmed["auto"]);
        Assert.False(Flag(confirmed, "readOnly"));
        Assert.True(Flag(submitted, "setByCommand"));
        Assert.Null(submitted["auto"]);
        Assert.False(Flag(submitted, "readOnly"));
    }

    [Fact]
    public void A_creator_stamp_no_command_writes_keeps_its_auto()
    {
        var requestedBy = Field("request", "requested_by");

        Assert.Equal("currentUser", (string?)requestedBy["auto"]);
        Assert.True(Flag(requestedBy, "readOnly"));
        Assert.False(Flag(requestedBy, "setByCommand"));
    }

    [Fact]
    public void A_required_field_the_command_also_writes_stays_on_the_form()
    {
        var reason = Field("request", "reason");

        Assert.False(Flag(reason, "setByCommand"));
    }

    [Fact]
    public void An_effect_aimed_at_a_referenced_record_owns_nothing_on_this_one()
    {
        var parentNote = Field("request", "parent_note");

        Assert.False(Flag(parentNote, "setByCommand"));
    }

    [Fact]
    public void A_collected_field_is_owned_without_touching_its_stamp()
    {
        var reason = Field("request", "approver", doc =>
        {
            var approve = ((JsonArray)doc["commands"]!).OfType<JsonObject>().First(c => (string?)c["key"] == "approve");
            approve["input"] = JsonNode.Parse("""{ "fields": ["approver"] }""");
        });

        Assert.True(Flag(reason, "setByCommand"));
        Assert.True(Flag(reason, "hideOnCreate"));
    }
}
