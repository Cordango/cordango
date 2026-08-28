// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class PageEditorSchemaTests
{
    private static JsonObject Page(string json) => (JsonObject)JsonNode.Parse(json)!;

    private const string RealisticPage = """
    { "key": "my_urgent", "label": "Urgent", "icon": "fire", "entity": "ticket", "blocks": [
        { "kind": "view", "view": "all_tickets" },
        { "kind": "row", "blocks": [
            { "kind": "stat", "source": { "entity": "ticket", "aggregate": { "op": "count" } } },
            { "kind": "chart", "chartType": "donut",
              "source": { "entity": "ticket", "aggregate": { "op": "count", "groupBy": "priority" } } } ] },
        { "kind": "table",
          "source": { "entity": "ticket",
                      "filters": [ { "field": "priority", "operator": "eq", "value": "high" } ],
                      "sort": [ { "field": "created_at", "direction": "desc" } ] },
          "fields": [ "subject", "priority" ] } ] }
    """;

    [Fact]
    public void It_accepts_and_rejects_exactly_what_the_save_gate_does()
    {
        foreach (var doc in new[]
        {
            RealisticPage,
            """{ "key": "my_x", "label": "X", "blocks": [] }""",
            """{ "key": "my.dots", "label": "X", "blocks": [] }""",
            """{ "key": "my_x", "label": "X", "blocks": [], "danger": 1 }""",
            """{ "label": "no key" }""",
            """{ "key": "my_x", "label": "X", "blocks": [ { "kind": "nonsense" } ] }""",
        })
            Assert.Equal(
                Gate.StructuralErrors(Page(doc), Schemas.PageSchema()).Count == 0,
                Gate.StructuralErrors(Page(doc), Schemas.PageEditorSchema()).Count == 0);
    }

    [Fact]
    public void Pruning_keeps_every_def_a_page_can_reach()
    {
        var pruned = Schemas.PageEditorSchema().AsObject();
        var defs = pruned["$defs"]!.AsObject();

        foreach (var name in Refs(pruned))
            Assert.True(defs[name] is not null, $"$ref to '{name}' has no definition");

        foreach (var need in new[] { "page", "block", "filter", "sort", "blockSource", "identifier", "tile", "tab" })
            Assert.True(defs[need] is not null, $"pruning dropped '{need}', which a page editor needs");

        foreach (var gone in new[] { "entity", "field", "workflow", "role", "process" })
            Assert.Null(defs[gone]);
    }

    [Fact]
    public void It_is_small_enough_to_ship_to_a_browser()
    {
        var pruned = Schemas.PageEditorSchema().ToJsonString().Length;
        Assert.InRange(pruned, 1, 96_500);
        Assert.True(pruned < Schemas.PageSchema().ToJsonString().Length);
    }

    private static IEnumerable<string> Refs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["$ref"]?.GetValue<string>() is { } r && r.StartsWith("#/$defs/"))
                    yield return r["#/$defs/".Length..];
                foreach (var kv in o)
                    foreach (var x in Refs(kv.Value)) yield return x;
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var x in Refs(item)) yield return x;
                break;
        }
    }
}
