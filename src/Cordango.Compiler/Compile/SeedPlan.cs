// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cordango.Compile;

/// <summary>One entity's place in a seed run.</summary>
/// <param name="Key">Entity key.</param>
/// <param name="Grid">The grid this entity is the CELL of, when the app composes one over it.</param>
public sealed record SeedStep(string Key, GridJoin? Grid = null);

/// <summary>An entity a composed grid renders in its cells, plus the axes its cell is keyed by. Seeding
/// the CROSS PRODUCT of those axes is what makes a grid full rather than a scattering of stray cells.
/// A grid needs ≥2 axes; either may be records or dates.</summary>
/// <param name="Entity">The cell entity (a competency_assessment, a shift).</param>
/// <param name="AxisFields">Reference fields bound to a RECORD axis, in axis order.</param>
/// <param name="Dates">A DATE axis, when one of the grid's axes is a date range rather than records.</param>
public sealed record GridJoin(string Entity, IReadOnlyList<string> AxisFields, DateAxis? Dates = null)
{
    public int AxisCount => AxisFields.Count + (Dates is null ? 0 : 1);
}

/// <summary>A grid axis that iterates dates, and the cell field that joins to it. The window matters: a
/// roster showing `{{today}}`+7 renders nothing at all if its shifts are seeded across ±45 days, which is
/// exactly why the generated planner came out empty.</summary>
public sealed record DateAxis(string Field, string From, string Step, int Count);

/// <summary>Works out WHAT to seed and in WHICH ORDER, from the manifest alone — no AI, no I/O, so it is
/// fully testable. Two jobs:
///
/// 1. <b>Order</b> — an entity's references must already exist before it can point at them, so entities
///    are emitted in dependency order (categories before competencies before assessments).
/// 2. <b>Grid joins</b> — read straight off the AUTHORED BLOCKS rather than guessed from entity shape.
///    A composed grid looks like `repeat as:'person'` ▸ `repeat as:'comp'` ▸ `repeat` over the cell
///    entity filtered on `{{person.id}}` AND `{{comp.id}}`. That innermost filter pair NAMES the join
///    entity and its two axes exactly. Shape-based guessing cannot do this: the competency matrix's cell
///    entity carries 5 references and 6 plain fields, so any "2 refs + few fields" rule misses it, while
///    a genuine 2-ref lookup that no grid renders would be cross-producted for nothing.
/// </summary>
public static class SeedPlan
{
    /// <summary>`{{person.id}}` / `{{day.date}}` → (scope name, key). A cell keys off the axis item's
    /// identity (a record's `id`) or its `date` (a date bucket).</summary>
    private static readonly Regex ScopeRef = new(@"^\{\{\s*(\w+)\.(id|date)\s*\}\}$", RegexOptions.Compiled);

    /// <summary>What an enclosing `repeat as:'x'` publishes: an entity's records, or a date range.</summary>
    private sealed record Scope(string? Entity, JsonObject? Dates);

    public static List<SeedStep> Build(JsonObject manifest)
    {
        var entities = (manifest["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var keys = entities.Select(e => Str(e, "key")).OfType<string>().ToList();
        var grids = FindGridJoins(manifest, keys);
        return Order(entities, keys)
            .Select(k => new SeedStep(k, grids.GetValueOrDefault(k)))
            .ToList();
    }

    /// <summary>Entities in dependency order: every non-platform reference target comes first. A reference
    /// CYCLE (a ↔ b) is broken by leaving the back-edge unsatisfied rather than deadlocking — that
    /// reference just seeds empty, which is honest and still yields a usable app.</summary>
    private static List<string> Order(List<JsonObject> entities, List<string> keys)
    {
        var deps = entities.ToDictionary(
            e => Str(e, "key")!,
            e => RefFields(e)
                .Select(f => Str(f, "targetEntity"))
                .OfType<string>()
                .Where(t => keys.Contains(t))
                .ToHashSet());

        var ordered = new List<string>();
        var seen = new HashSet<string>();
        // Deterministic: repeatedly take every entity whose deps are all placed. If a pass places
        // nothing, the rest are in a cycle — emit them in declaration order and move on.
        while (ordered.Count < keys.Count)
        {
            var ready = keys.Where(k => !seen.Contains(k)
                                        && deps[k].All(d => seen.Contains(d) || d == k)).ToList();
            if (ready.Count == 0) ready = keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var k in ready) { ordered.Add(k); seen.Add(k); }
        }
        return ordered;
    }

    /// <summary>Walk every authored block tree, tracking the `as` scopes in force, and record each repeat
    /// whose source entity is filtered by two or more `{{scope.id}}` values — that is a grid cell.</summary>
    private static Dictionary<string, GridJoin> FindGridJoins(JsonObject manifest, List<string> keys)
    {
        var found = new Dictionary<string, GridJoin>();

        void Walk(JsonNode? node, Dictionary<string, Scope> scopes)
        {
            switch (node)
            {
                case JsonArray arr:
                    foreach (var n in arr) Walk(n, scopes);
                    return;
                case JsonObject b:
                    var next = scopes;
                    if (Str(b, "kind") == "repeat")
                    {
                        var src = b["source"] as JsonObject;
                        var entity = Str(src, "entity");

                        // Axis filters: `field` is a field on the CELL entity and `value` names an
                        // enclosing repeat's `as` scope — `{{person.id}}` keys off that row's record,
                        // `{{day.date}}` off that column's date. Two or more axes ⇒ this repeat is a cell.
                        // A dotted `path` filter is skipped on purpose: it joins THROUGH a relation, so
                        // the cell's own field cannot be set directly and there is no cross product.
                        var axes = new List<string>();
                        DateAxis? dates = null;
                        foreach (var fn in (src?["filters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        {
                            var field = Str(fn, "field");
                            var m = ScopeRef.Match(Str(fn, "value") ?? "");
                            if (field is null || !m.Success) continue;
                            if (!scopes.TryGetValue(m.Groups[1].Value, out var scope)) continue;

                            if (m.Groups[2].Value == "id" && scope.Entity is { Length: > 0 }) axes.Add(field);
                            else if (m.Groups[2].Value == "date" && scope.Dates is { } spec && dates is null)
                                dates = new DateAxis(field, Str(spec, "from") ?? "{{today}}",
                                    Str(spec, "step") ?? "day", Int(spec, "count") ?? 7);
                        }
                        var join = new GridJoin(entity ?? "", axes, dates);
                        if (entity is not null && keys.Contains(entity) && join.AxisCount >= 2
                            && !found.ContainsKey(entity))
                            found[entity] = join;

                        // Publish this repeat's scope for its descendants.
                        if (Str(b, "as") is { } name)
                            next = new Dictionary<string, Scope>(scopes)
                            { [name] = new Scope(entity, src?["dates"] as JsonObject) };
                    }
                    foreach (var k in new[] { "blocks", "tabs", "columns" }) Walk(b[k], next);
                    return;
            }
        }

        foreach (var page in (manifest["pages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            Walk(page["blocks"], new Dictionary<string, Scope>());
        foreach (var ent in (manifest["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (ent["detail"] is JsonObject det) Walk(det["blocks"], new Dictionary<string, Scope>());
        return found;
    }

    private static int? Int(JsonObject? o, string k) =>
        o?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static IEnumerable<JsonObject> RefFields(JsonObject entity) =>
        (entity["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(f => Str(f, "type") == "reference");

    /// <summary>Reads a string property, tolerating a non-string value. A filter's `value` is legitimately
    /// a bool/number (`{"field":"active","value":true}`), so a bare GetValue&lt;string&gt;() throws on real
    /// manifests.</summary>
    private static string? Str(JsonObject? o, string k) =>
        o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
