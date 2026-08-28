// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// A figure worked out from OTHER records: how many periods a scenario has, what its rounds add up
/// to, what payroll lands in one month.
///
/// <para><b>Each one is a query, and that is the whole of it.</b> A rollup names a child entity, the
/// way it relates to this record, and an operation — so it becomes one <c>Where</c> chain and one
/// <c>SumAsync</c>. There is no aggregate engine here because there is nothing for one to decide.</para>
///
/// <para><b>And the recompute is straight-line, because the generator knows the graph.</b> The
/// platform meets a definition it has never seen and needs a general cascade with cycle detection to
/// survive it. This does not: the whole rollup graph is in the definition at build time, it is
/// shallow, and <see cref="RollupGraph"/> refuses to emit at all if it is ever cyclic. What comes out
/// is a call chain somebody can read, not machinery that works one out at run time.</para>
/// </summary>
public static class RollupEmitter
{
    /// <summary>The two operations the language has. Anything else is reported rather than guessed
    /// at — an average that silently became a sum would be a wrong figure nobody could see.</summary>
    public static readonly IReadOnlySet<string> Ops =
        new HashSet<string>(StringComparer.Ordinal) { "sum", "count" };

    /// <summary>Comparisons a rollup's own filters may use. Deliberately small: these go to the
    /// DATABASE, and a filter this cannot write must stop the rollup rather than widen it.</summary>
    private static readonly IReadOnlySet<string> Operators =
        new HashSet<string>(StringComparer.Ordinal) { "eq", "neq" };

    /// <summary>Every rollup on this entity the generator can write, in definition order.</summary>
    public static IReadOnlyList<(FieldModel Field, string Query)> Rollups(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var found = new List<(FieldModel, string)>();
        foreach (var field in entity.AuthoredFields)
            if (Query(app, entity, field) is { } query)
                found.Add((field, query));

        return found;
    }

    /// <summary>
    /// One rollup as the query that answers it, or null when this generator cannot write it.
    /// </summary>
    public static string? Query(AppModel app, EntityModel parent, FieldModel field)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(field);

        if (field.Computed?["rollup"] is not JsonObject rollup) return null;

        var op = AppModel.Str(rollup["op"]);
        if (op is null || !Ops.Contains(op)) return null;

        if (app.Entity(AppModel.Str(rollup["entity"])) is not { } child) return null;

        var where = Predicates(rollup, parent, child);
        if (where is null) return null;

        var chain = $"db.Set<{child.TypeName}>()" + string.Concat(where);

        if (op == "count") return Narrow(field, $"await {chain}.CountAsync(ct)", counted: true);

        // A sum needs something to add up, and the child has to have it.
        if (child.Field(AppModel.Str(rollup["field"])) is not { } summed) return null;

        // `(decimal?)` inside the selector, so an empty set sums to null rather than to zero and the
        // narrowing below decides what that means. Summing `long` would also do integer arithmetic.
        return Narrow(field, $"await {chain}.SumAsync(x => (decimal?)x.{summed.PropertyName}, ct)", counted: false);
    }

    /// <summary>
    /// How the child relates to this record, and which of its rows count.
    ///
    /// <para>Two shapes, and the discriminator is <c>match</c>. Without it, <c>via</c> is the child's
    /// own reference to this record — a round belongs to a scenario. With it, the two are siblings
    /// under something else and <c>match</c> is the field they share: a hiring line and a period both
    /// belong to a scenario, and what makes the line count towards THAT period is the window.</para>
    ///
    /// <para>Null when any part cannot be written, and null takes the whole rollup with it. A
    /// predicate quietly dropped is not a smaller answer, it is a bigger one — a period would collect
    /// every hiring line in the company.</para>
    /// </summary>
    private static List<string>? Predicates(JsonObject rollup, EntityModel parent, EntityModel child)
    {
        var where = new List<string>();

        if (AppModel.Str(rollup["match"]) is { } match)
        {
            if (parent.Field(match) is not { } here || child.Field(match) is not { } there) return null;
            where.Add($".Where(x => x.{there.PropertyName} == r.{here.PropertyName})");

            // A matched rollup without a window would collect every sibling, which is a different
            // figure from the one asked for.
            if (rollup["window"] is not JsonObject window) return null;
            if (Window(window, parent, child) is not { } bounded) return null;
            where.Add(bounded);
        }
        else
        {
            if (child.Field(AppModel.Str(rollup["via"])) is not { } via) return null;
            where.Add($".Where(x => x.{via.PropertyName} == r.Id)");
        }

        foreach (var filter in AppModel.Arr(rollup["filters"]).OfType<JsonObject>())
        {
            if (Filter(filter, child) is not { } written) return null;
            where.Add(written);
        }

        return where;
    }

    /// <summary>
    /// Which of the child's rows count towards THIS record.
    ///
    /// <para><b>Two forms, and they point in opposite directions.</b> A DATED row has one value and
    /// this record has the bucket: a cohort month lands in the period whose sequence it matches.
    /// A SPANNING row has the range and this record has the single value it must cover: a hiring
    /// line runs from one month to another, and it counts towards every period whose date falls
    /// inside that. Reading the second like the first is the mistake, and it is a quiet one — the
    /// comparison still compiles and still returns rows, just the wrong ones.</para>
    ///
    /// <para><b>An empty bound is open, not missing.</b> The schema says so in as many words: a
    /// hiring line with no start month has always been running, and one with no end month never
    /// stops. Treating either as a failed comparison would drop exactly the rows that count
    /// most.</para>
    /// </summary>
    private static string? Window(JsonObject window, EntityModel parent, EntityModel child)
    {
        // Dated: one value on the child, a bucket on this record.
        if (window["within"] is JsonObject within)
        {
            if (child.Field(AppModel.Str(window["at"])) is not { } at) return null;

            // "A row with no value lands in no bucket" — including this one.
            var bounds = new List<string> { $"x.{at.PropertyName} != null" };

            if (AppModel.Str(within["from"]) is { } lower)
            {
                if (parent.Field(lower) is not { } from) return null;
                bounds.Add($"x.{at.PropertyName} >= r.{from.PropertyName}");
            }

            if (AppModel.Str(within["to"]) is { } upper)
            {
                if (parent.Field(upper) is not { } to) return null;
                bounds.Add($"x.{at.PropertyName} <= r.{to.PropertyName}");
            }

            // At least one, or the bucket is every row there is.
            return bounds.Count == 1 ? null : $".Where(x => {string.Join(" && ", bounds)})";
        }

        // Spanning: a range on the child, one value on this record it has to cover.
        if (parent.Field(AppModel.Str(window["against"])) is not { } against) return null;

        var spans = new List<string>();

        if (AppModel.Str(window["from"]) is { } starts)
        {
            if (child.Field(starts) is not { } start) return null;
            spans.Add($"(x.{start.PropertyName} == null "
                + $"|| x.{start.PropertyName} <= r.{against.PropertyName})");
        }

        if (AppModel.Str(window["to"]) is { } ends)
        {
            if (child.Field(ends) is not { } end) return null;
            spans.Add($"(x.{end.PropertyName} == null "
                + $"|| x.{end.PropertyName} >= r.{against.PropertyName})");
        }

        return spans.Count == 0 ? null : $".Where(x => {string.Join(" && ", spans)})";
    }

    private static string? Filter(JsonObject filter, EntityModel child)
    {
        if (child.Field(AppModel.Str(filter["field"])) is not { } field) return null;

        var op = AppModel.Str(filter["operator"]);
        if (op is null || !Operators.Contains(op)) return null;

        var comparison = op == "eq" ? "==" : "!=";
        return $".Where(x => x.{field.PropertyName} {comparison} {Naming.Literal(AppModel.Str(filter["value"]))})";
    }

    /// <summary>
    /// The aggregate as the column's own type.
    ///
    /// <para>A sum over no rows is null, and a count over none is zero — which is the honest pair. No
    /// rounds yet is not "nothing raised", it is a figure nobody has stated; no periods yet IS zero
    /// periods.</para>
    /// </summary>
    private static string Narrow(FieldModel field, string call, bool counted) =>
        (field.ClrType, counted) switch
        {
            ("long", true) => $"(long?){call}",
            ("long", false) => $"(long?)({call})",
            (_, true) => $"(decimal?){call}",
            _ => call,
        };
}

/// <summary>
/// Which entities aggregate which, and therefore what has to be worked out again when a record
/// changes.
///
/// <para><b>Known at build time, which is why there is no engine.</b> Every rollup in the definition
/// names the entity it counts, so the whole graph is a walk over the manifest. It is acyclic in every
/// application anybody has written — and where it is not, this refuses rather than emitting a chain
/// that would recurse until the stack ran out.</para>
/// </summary>
public static class RollupGraph
{
    /// <summary>Parent entity key to the child entity keys it aggregates.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Edges(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var parent in app.Entities)
            foreach (var field in parent.AuthoredFields)
                if (RollupEmitter.Query(app, parent, field) is not null
                    && AppModel.Str(field.Computed?["rollup"]?["entity"]) is { } child)
                {
                    if (!edges.TryGetValue(parent.Key, out var set))
                        edges[parent.Key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(child);
                }

        return edges.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when some entity transitively aggregates itself.
    ///
    /// <para>Nothing in the corpus does, and an application that did could not be given an answer:
    /// a total that is an input to itself has no fixed point the generator could compute towards.
    /// Reported rather than emitted.</para>
    /// </summary>
    public static bool IsCyclic(AppModel app)
    {
        var edges = Edges(app);
        var settled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in edges.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (Walk(start, [])) return true;

        return false;

        bool Walk(string node, HashSet<string> path)
        {
            if (settled.Contains(node)) return false;
            if (!path.Add(node)) return true;

            if (edges.TryGetValue(node, out var children))
                foreach (var child in children.OrderBy(k => k, StringComparer.Ordinal))
                    if (Walk(child, path)) return true;

            path.Remove(node);
            settled.Add(node);
            return false;
        }
    }

    /// <summary>The entities that aggregate this one DIRECTLY, in a stable order.</summary>
    public static IReadOnlyList<EntityModel> Parents(AppModel app, EntityModel child)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(child);

        var edges = Edges(app);
        return
        [
            .. edges.Where(p => p.Value.Contains(child.Key))
                .Select(p => app.Entity(p.Key))
                .OfType<EntityModel>()
                .OrderBy(e => e.Key, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Every aggregating entity, ordered so that one comes after everything it counts.
    ///
    /// <para><b>Working the whole application out is a different problem from keeping it right.</b>
    /// The per-record cascade starts at a row and walks UP, which is exactly right for one write and
    /// quadratic for a whole table — a scenario would be recomputed once per round underneath it.
    /// This visits each level exactly once instead.</para>
    ///
    /// <para>And a level is only correct once what it counts has settled, because a total is a QUERY:
    /// a scenario that sums its periods has to run after the periods that sum their lines, or it
    /// sums the values they held before. So the order is the graph's, deepest first.</para>
    ///
    /// <para><see cref="IsCyclic"/> is what makes this a sort rather than a search — a definition
    /// that cycles is refused before anything is emitted.</para>
    /// </summary>
    public static IReadOnlyList<EntityModel> RecomputeOrder(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var edges = Edges(app);
        var aggregating = app.Entities
            .Where(e => RollupEmitter.Rollups(app, e).Count > 0)
            .ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);

        var ordered = new List<EntityModel>();
        var settled = new HashSet<string>(StringComparer.Ordinal);

        // Definition order for the roots, so the emitted call order matches the emitted method order.
        foreach (var entity in app.Entities)
            if (aggregating.ContainsKey(entity.Key)) Visit(entity.Key);

        return ordered;

        void Visit(string key)
        {
            if (!settled.Add(key)) return;

            // What this one counts, first. An entity that aggregates nothing which itself aggregates
            // is a leaf here, whatever else hangs off it.
            if (edges.TryGetValue(key, out var children))
                foreach (var child in children.OrderBy(k => k, StringComparer.Ordinal))
                    if (aggregating.ContainsKey(child)) Visit(child);

            if (aggregating.TryGetValue(key, out var entity)) ordered.Add(entity);
        }
    }

    /// <summary>
    /// How to find the parents of a changed child: the query, given <c>record</c>.
    ///
    /// <para>Two shapes again, and the same discriminator. A round names its scenario, so there is
    /// one parent and its id is on the record. A hiring line names no period at all — it and the
    /// periods are siblings under a scenario — so every period of that scenario is a candidate and
    /// the window decides which of them the figure lands in. Recomputing all of them is the honest
    /// answer: which ones the line falls into is exactly what changed.</para>
    /// </summary>
    public static string? Affected(AppModel app, EntityModel parent, EntityModel child)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        foreach (var field in parent.AuthoredFields)
        {
            if (field.Computed?["rollup"] is not JsonObject rollup) continue;
            if (AppModel.Str(rollup["entity"]) != child.Key) continue;
            if (RollupEmitter.Query(app, parent, field) is null) continue;

            if (AppModel.Str(rollup["match"]) is { } match)
            {
                if (parent.Field(match) is { } here && child.Field(match) is { } there)
                    return $"x => x.{here.PropertyName} == record.{there.PropertyName}";
            }
            else if (child.Field(AppModel.Str(rollup["via"])) is { } via)
            {
                return $"x => x.Id == record.{via.PropertyName}";
            }
        }

        return null;
    }
}
