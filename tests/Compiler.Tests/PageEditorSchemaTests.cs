// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <see cref="Schemas.PageEditorSchema"/> — what a client-side page editor derives its property
/// editors from.
///
/// <para>The property worth defending: it is the SAME document the save endpoint validates against,
/// only lighter. An editor built from a hand-written description of "what a block can have" drifts
/// from the validator on the first schema change and the user discovers it at save time. So these
/// pin that pruning changed nothing a page can reach, and that a page which validates against one
/// validates against the other.</para>
/// </summary>
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
        // Not "both are reasonable" — the same verdict, because they are the same document. If these
        // ever disagree, an editor built on this one is lying to the user about what will save.
        foreach (var doc in new[]
        {
            RealisticPage,
            """{ "key": "my_x", "label": "X", "blocks": [] }""",
            """{ "key": "my.dots", "label": "X", "blocks": [] }""",         // identifier pattern
            """{ "key": "my_x", "label": "X", "blocks": [], "danger": 1 }""", // closed shape
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

        // Every $ref resolves — a dangling one would surface in the editor as a property with no
        // editor at all, silently.
        foreach (var name in Refs(pruned))
            Assert.True(defs[name] is not null, $"$ref to '{name}' has no definition");

        // The shapes a block editor is entirely built out of.
        foreach (var need in new[] { "page", "block", "filter", "sort", "blockSource", "identifier", "tile", "tab" })
            Assert.True(defs[need] is not null, $"pruning dropped '{need}', which a page editor needs");

        // And it dropped the document vocabulary a page cannot contain.
        foreach (var gone in new[] { "entity", "field", "workflow", "role", "process" })
            Assert.Null(defs[gone]);
    }

    [Fact]
    public void It_is_small_enough_to_ship_to_a_browser()
    {
        // Measured 2026-07-28: 109,381 → 79,356 (~12 KB gzipped). Pruning wins less here than it does
        // on the generator's tool schemas, and that is the honest answer rather than a target missed:
        // a page's block tree reaches nearly the whole block/filter/source/effect vocabulary, so most
        // of the table IS reachable. What it drops is the document vocabulary (entity/field/workflow/
        // role/process) a page cannot contain.
        // The payload is fetched once, lazily, only when the editor opens, and cached for an hour.
        // Ceiling, not a target: if this trips, ask what new vocabulary a page just gained.
        //
        // 2026-08-07, re-measured at 91,175: the day view (block_calendar gained `endField`,
        // `range: "day"` and a shared `timeAxis`) is what a page just gained, and it accounts for
        // ~1.7 KB of that. The other ~10 KB is drift since July that nothing re-measured — the
        // ceiling had 600 bytes of headroom left before this change touched it. Raised to 96,000 so
        // it keeps being a tripwire rather than a formality; the July gzipped figure is stale and
        // should be re-measured the next time this is looked at.
        var pruned = Schemas.PageEditorSchema().ToJsonString().Length;
        Assert.InRange(pruned, 1, 96_000);
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
