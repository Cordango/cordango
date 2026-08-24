// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;

namespace Cordango.Compiler.Tests;

/// <summary>
/// Which boards may be dragged.
///
/// <para>The design pass marks every board <c>interactive</c>, so the compiler decides from the
/// grouping field instead of taking its word for it. What counts as writable is the whole question,
/// and it was answered wrongly for the case boards exist for: a process-governed status was treated
/// as unwritable and every kanban view over one was rewritten to read-only, which made the
/// language's own description of a process board — dragging a card runs the matching transition —
/// something no definition could ask for.</para>
/// </summary>
public class BoardInteractionTests
{
    private static JsonObject Doc() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0",
      "entities":[
        { "key":"expense","label":"Expense","displayField":"title",
          "fields":[
            {"key":"title","label":"Title","type":"text"},
            {"key":"stage","label":"Stage","type":"select","role":"status"},
            {"key":"team","label":"Team","type":"select",
             "options":[{"value":"a","label":"A"},{"value":"b","label":"B"}]},
            {"key":"tier","label":"Tier","type":"select","readOnly":true,
             "options":[{"value":"x","label":"X"},{"value":"y","label":"Y"}]}
          ] }
      ],
      "processes":[
        { "key":"approval","entity":"expense","stateField":"stage","initialState":"draft",
          "states":[{"key":"draft","label":"Draft"},{"key":"submitted","label":"Submitted"}],
          "transitions":[
            {"key":"submit","label":"Submit","from":["draft"],"to":"submitted","command":"submit_expense"}
          ] }
      ],
      "commands":[
        {"key":"submit_expense","label":"Submit","entity":"expense","effects":[]}
      ],
      "views":[
        {"key":"by_stage","label":"By stage","type":"kanban","entity":"expense",
         "config":{"groupByField":"stage"}},
        {"key":"by_team","label":"By team","type":"kanban","entity":"expense",
         "config":{"groupByField":"team"}},
        {"key":"by_tier","label":"By tier","type":"kanban","entity":"expense",
         "config":{"groupByField":"tier"}}
      ]
    }
    """)!;

    private static string? InteractionOf(string view)
    {
        var manifest = AppCompiler.Compile(Doc(), "app", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var found = manifest["views"]!.AsArray()
            .OfType<JsonObject>()
            .Single(v => (string?)v["key"] == view);

        return (string?)found["config"]?["interaction"];
    }

    [Fact]
    public void A_board_over_a_process_governed_status_stays_draggable() =>
        Assert.NotEqual("visualization", InteractionOf("by_stage"));

    [Fact]
    public void A_board_over_an_ordinary_select_stays_draggable() =>
        Assert.NotEqual("visualization", InteractionOf("by_team"));

    /// <summary>Nothing writes a read-only field, transition or not, so the promise a drag makes
    /// cannot be kept.</summary>
    [Fact]
    public void A_board_over_a_read_only_field_is_read_only() =>
        Assert.Equal("visualization", InteractionOf("by_tier"));
}
