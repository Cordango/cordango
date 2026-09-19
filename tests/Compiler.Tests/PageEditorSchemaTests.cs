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

    /// <summary>
    /// The page editor's schema goes over the wire to a browser, so its size is a real cost rather
    /// than an abstraction.
    ///
    /// <para>The ceiling is a canary for DUPLICATION — a shared shape inlined across variants — not a
    /// cap on vocabulary. A move is allowed and must be recorded with its cause:</para>
    /// <list type="bullet">
    /// <item>96,500 → 97,400: `action.entity` + `action.keys`, the self-anchoring form that lets a
    /// button name its own record. +641 bytes after the descriptions were trimmed to match their
    /// neighbours; two properties on one variant, nothing shared was inlined.</item>
    /// <item>97,400 → 97,700: `intake.via`, the record-bound form — a questionnaire answered against
    /// the record it sits on. One property on one variant plus a sentence in the block's own
    /// description; nothing shared was inlined.</item>
    /// <item>97,700 → 98,900: the `column` def — a list column may now fix its width and say whether
    /// a long value clips or wraps. +1,199 bytes, and the shape is SHARED: one def referenced by
    /// table, split and child rather than the same two properties written into each. That is the
    /// direction this canary exists to protect, so the move is the def's own size, not duplication.
    /// </item>
    /// <item>98,900 → 99,400: `justify` on `row` and `stack` — the MAIN axis, where `align` is the
    /// cross one. +481 bytes: the same property inlined on two variants, exactly as `align` already
    /// is, with its description already cut to the contract sentence. Nothing shared was inlined,
    /// and a shared def for two enum properties would cost more than it saves.</item>
    /// <item>99,400 → 99,900: <c>blockSource.app</c> — a `repeat` over ANOTHER app's rows, so a
    /// timesheet's week grid can iterate the projects the Projects app owns instead of keeping a
    /// second copy of them. +372 bytes, paid ONCE: <c>blockSource</c> is a shared <c>$def</c> that
    /// every surface taking a source already references, which is the shape this canary is for. The
    /// description carries the three refusals (repeat only, entity origin only, no hop) because an
    /// author who learns them from a gate error has already written the wrong thing.</item>
    /// <item>99,900 → 100,400: <c>card.openDetail</c> — a card opens the QUICK LOOK of the record it
    /// is bound to, the same gesture <c>cell</c> already had. +397 bytes on one variant. It exists
    /// because a grid ROW cannot carry the gesture: <c>PrimRepeat</c> refuses to make a row holding
    /// cells clickable, so every misclick in the gap beside a cell would navigate. The box that shows
    /// the record says so itself instead.</item>
    /// <item>100,400 → 101,500: <c>column.source</c> — a table column that is a FIGURE reduced out of
    /// another app ("hours logged against this project") rather than a field of these rows, plus the
    /// <c>label</c> it needs because it has no field to name it. +1,044 bytes, and it is a nested
    /// object on the SHARED <c>column</c> def — one shape referenced by table, split and child, not
    /// three copies. The aggregate sub-object carries its own enum and required list, which is most
    /// of the size: the alternative was a loose object, and a column that silently reduced nothing
    /// is exactly what a contract is for.</item>
    /// <item>101,500 &#8594; 104,000 (2026-09-14): <c>class</c>, a Vuetify utility-class string on the
    /// outer element of EVERY block kind &#8212; the appearance vocabulary that only the four layout
    /// boxes had before, extended to the table, chart, calendar, board, gantt and form a demo actually
    /// shows. +2,465 bytes, to 103,909. It is ONE shared <c>blockClass</c> <c>$def</c> referenced 41
    /// times, not a set of properties written into 41 closed variants; this schema ships FULL
    /// descriptions, so the difference is the whole reason it still fits &#8212; the prose is paid
    /// once here rather than per variant.</item>
    /// <item>104,000 &#8594; 104,200 (2026-09-14, same day): <c>class</c> on the seven authorable
    /// shapes that are not blocks &#8212; <c>column</c>, <c>tab</c>, <c>page</c>, <c>formBlock</c>,
    /// <c>command</c>, <c>process.states[]</c> and <c>field</c>. +152 bytes, to 104,061. Seven new
    /// styling surfaces for seven <c>$ref</c>s, because the shape they point at was already here.
    /// </item>
    /// <item>104,200 &#8594; 105,700 (2026-09-18): <c>documents</c>, the 42nd block kind &#8212; the
    /// platform's Documents app rendered on its own page or, with <c>field</c> naming a record's
    /// reference to its space, as the documentation that record owns. +1,546 bytes, to 105,607: one
    /// closed variant with one binding property and one prose property. The description carries both
    /// homes because "a page takes no field" is otherwise the first thing an author meets, as a gate
    /// error, after writing the wrong one.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void It_is_small_enough_to_ship_to_a_browser()
    {
        var pruned = Schemas.PageEditorSchema().ToJsonString().Length;
        Assert.InRange(pruned, 1, 105_700);
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
