// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class SynthesizedCommandGateTests
{
    private static JsonObject App(string detailBlocks, string? transitionCommand = null) =>
        (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion": "2.0", "key": "app", "name": "App", "version": "1.0.0",
          "entities": [
            { "key": "task", "label": "Task", "labelPlural": "Tasks", "displayField": "title",
              "fields": [
                { "key": "title", "label": "Title", "type": "text", "required": true },
                { "key": "stage", "label": "Stage", "type": "select", "role": "status",
                  "options": [ { "value": "todo", "label": "To do" },
                               { "value": "done", "label": "Done" } ] }
              ],
              "detail": { "blocks": {{detailBlocks}} } }
          ],
          "processes": [
            { "key": "task_flow", "entity": "task", "stateField": "stage", "initialState": "todo",
              "states": [ { "key": "todo", "label": "To do" },
                          { "key": "done", "label": "Done", "terminal": true } ],
              "transitions": [
                { "key": "complete", "label": "Complete", "from": ["todo"], "to": "done"
                  {{(transitionCommand is null ? "" : $", \"command\": \"{transitionCommand}\"")}} }
              ] }
          ],
          "pages": [
            { "key": "tasks", "label": "Tasks", "entity": "task",
              "blocks": [ { "kind": "table", "source": { "entity": "task" } },
                          { "kind": "create", "entity": "task" } ] }
          ]
        }
        """)!;

    private static string[] CommandErrors(JsonObject doc) =>
        [.. Gate.Validate(doc).Where(e => e.Contains("command", StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public void An_action_block_may_name_the_command_a_transition_gets_automatically()
    {
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": ["task_complete"] },
              { "kind": "action", "command": "task_complete", "label": "Done" } ]
            """);

        Assert.Empty(CommandErrors(doc));
    }

    [Fact]
    public void The_key_is_entity_qualified_so_two_entities_may_share_a_transition_name()
    {
        Assert.Equal("claim_approve", ProcessCommands.SynthesizedKey("claim", "approve"));
        Assert.NotEqual(ProcessCommands.SynthesizedKey("claim", "approve"),
                        ProcessCommands.SynthesizedKey("invoice", "approve"));
    }

    [Fact]
    public void A_command_that_does_not_exist_is_still_refused()
    {
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": [] },
              { "kind": "action", "command": "task_archive", "label": "Archive" } ]
            """);

        Assert.Contains(CommandErrors(doc),
            e => e.Contains("task_archive", StringComparison.Ordinal));
    }

    [Fact]
    public void A_transition_that_names_its_own_command_gets_no_synthesized_one()
    {
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": [] },
              { "kind": "action", "command": "task_complete", "label": "Done" } ]
            """, transitionCommand: "finish_it");

        Assert.Contains(CommandErrors(doc),
            e => e.Contains("task_complete", StringComparison.Ordinal));
    }

    [Fact]
    public void A_synthesized_command_owes_nothing_to_a_hub()
    {
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": [] } ]
            """);

        Assert.Empty(CommandErrors(doc));
    }
}
