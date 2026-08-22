// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Xunit;

namespace Cordango.Compiler.Tests;

public class SalvageTests
{
    private static JsonObject Doc() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "helpdesk", "name": "Helpdesk", "version": "1.0.0",
      "entities": [
        { "key": "ticket", "label": "Ticket", "displayField": "subject",
          "fields": [
            { "key": "subject", "label": "Subject", "type": "text", "required": true },
            { "key": "stage", "label": "Stage", "type": "select", "role": "status" }
          ] }
      ],
      "processes": [
        { "key": "ticket_flow", "entity": "ticket", "stateField": "stage", "initialState": "open",
          "states": [ { "key": "open", "label": "Open" }, { "key": "closed", "label": "Closed", "terminal": true } ],
          "transitions": [ { "key": "close", "label": "Close", "from": ["open"], "to": "closed", "command": "close_ticket" } ] }
      ],
      "commands": [
        { "key": "close_ticket", "label": "Close", "entity": "ticket",
          "effects": [ { "type": "notify", "to": "{{actor.id}}", "message": "Closed {{record.subject}}" } ] }
      ],
      "roles": [
        { "key": "admin", "name": "Admin", "grants": [ { "entity": "*", "read": true, "commands": ["*"] } ] },
        { "key": "agent", "name": "Agent",
          "grants": [ { "entity": "ticket", "read": true, "commands": ["close_ticket"] } ] }
      ],
      "workflows": [
        { "key": "notify_close", "name": "Notify on close",
          "trigger": { "event": "record.updated", "entity": "ticket" },
          "effects": [ { "type": "notify", "to": "{{record.subject}}", "message": "closed" } ] },
        { "key": "daily_digest", "name": "Daily digest",
          "trigger": { "event": "schedule", "entity": "ticket" },
          "effects": [ { "type": "notify", "to": "{{actor.id}}", "message": "digest" } ] }
      ]
    }
    """)!;

    [Fact]
    public void Drops_the_blamed_workflow_and_the_result_re_gates_clean()
    {
        var doc = Doc();
        var errors = Gate.Validate(doc);
        Assert.Single(errors);
        Assert.Contains("/workflows/1", errors[0]);

        var (salvaged, dropped) = Salvage.TryDropFailingStructures(doc, errors);

        Assert.NotNull(salvaged);
        Assert.Equal(new[] { "workflow 'daily_digest'" }, dropped);
        Assert.Single(salvaged!["workflows"]!.AsArray());
        Assert.Empty(Gate.Validate(salvaged));
    }

    [Fact]
    public void Dropping_a_command_unbinds_its_transition_and_grants()
    {
        var doc = Doc();
        ((JsonArray)doc["workflows"]!).RemoveAt(1);
        var errors = new List<string> { "SEMANTIC: command 'close_ticket' effect[0] template token '{{record.nope}}' — 'nope' is not a field of 'ticket'" };

        var (salvaged, dropped) = Salvage.TryDropFailingStructures(doc, errors);

        Assert.NotNull(salvaged);
        Assert.Contains("command 'close_ticket'", dropped);
        Assert.Empty(salvaged!["commands"]!.AsArray());
        var transition = salvaged["processes"]![0]!["transitions"]![0]!.AsObject();
        Assert.False(transition.ContainsKey("command"));
        Assert.Empty(salvaged["roles"]![1]!["grants"]![0]!["commands"]!.AsArray());
        Assert.Single(salvaged["roles"]![0]!["grants"]![0]!["commands"]!.AsArray());
        Assert.Empty(Gate.Validate(salvaged));
    }

    [Fact]
    public void Refuses_errors_it_cannot_attribute_to_a_droppable_structure()
    {
        var doc = Doc();
        var (salvaged, dropped) = Salvage.TryDropFailingStructures(doc, new List<string>
        {
            "SEMANTIC: workflow 'daily_digest' condition field 'x' is not a field of 'ticket'",
            "SEMANTIC: entity 'ticket' displayField 'nope' is not a field of 'ticket'",
        });
        Assert.Null(salvaged);
        Assert.Empty(dropped);

        var (none, _) = Salvage.TryDropFailingStructures(doc, new List<string> { "STRUCTURAL at [/entities/0/label]: bad" });
        Assert.Null(none);
    }
}
