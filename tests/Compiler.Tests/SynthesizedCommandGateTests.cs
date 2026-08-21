// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// A block may reference the command a transition gets automatically.
///
/// <para><b>It could not until 2026-08-13, and the reason was an ordering accident rather than a
/// decision.</b> <c>AppCompiler</c> synthesizes a command for every command-less transition so that
/// every legal state move has an invokable button — but into the MANIFEST, and
/// <c>Gate.Validate</c> runs on the DEFINITION, first:</para>
///
/// <code>
/// Gate.Validate(definition)   ← "action command 'task_complete' is not a command on 'task'"
/// DesignDefaults.Apply
/// Gate.Validate(definition)
/// AppCompiler.Compile         ← task_complete created here, too late to be referenced
/// </code>
///
/// <para>Found by a live agent building a task app: it wanted a Done button on a card, could not
/// bind one to the lifecycle's own transition, and authored a duplicate command that did the same
/// thing — leaving two identical buttons on the record page. It reported the duplication as a gap and
/// it was right.</para>
/// </summary>
public class SynthesizedCommandGateTests
{
    /// <param name="detailBlocks">The record detail's block tree — where the action button goes.</param>
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
        // `task_complete` appears nowhere in this document. It is what the compiler will build for
        // the `complete` transition, and the gate now knows that.
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": ["task_complete"] },
              { "kind": "action", "command": "task_complete", "label": "Done" } ]
            """);

        Assert.Empty(CommandErrors(doc));
    }

    [Fact]
    public void The_key_is_entity_qualified_so_two_entities_may_share_a_transition_name()
    {
        // Command keys are unique per entity, not globally: a claim and an invoice may both have an
        // `approve` transition, and they are different buttons.
        Assert.Equal("claim_approve", ProcessCommands.SynthesizedKey("claim", "approve"));
        Assert.NotEqual(ProcessCommands.SynthesizedKey("claim", "approve"),
                        ProcessCommands.SynthesizedKey("invoice", "approve"));
    }

    [Fact]
    public void A_command_that_does_not_exist_is_still_refused()
    {
        // The registration must not turn the check off. `task_archive` is not a transition and not
        // authored, so naming it is still a mistake worth catching.
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
        // Authored commands win — the compiler skips a transition that names one, so the gate must
        // not invent a second key for it. `task_complete` should NOT resolve here.
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
        // The regression this nearly shipped with. Once the gate knew these commands existed,
        // ValidateRecordHeaderCommands started demanding they appear in the hub's `actions` — and
        // three corpus apps failed for buttons nobody had authored. A transition-bound command is
        // placed by its process, not by the hub, which is why the exemption already existed for
        // explicitly-bound ones and now covers synthesized ones too.
        var doc = App("""
            [ { "kind": "hub", "facts": ["stage"], "actions": [] } ]
            """);

        Assert.Empty(CommandErrors(doc));
    }
}
