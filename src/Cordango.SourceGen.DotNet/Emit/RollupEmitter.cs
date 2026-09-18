// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNet.Emit;

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
    /// <summary>
    /// The operations the language has, all of which this target now writes.
    ///
    /// <para>Anything outside this set is reported rather than guessed at — an average that silently
    /// became a sum would be a wrong figure nobody could see. For a while the set was <c>sum</c> and
    /// <c>count</c> alone while the schema's enum already held five, so <c>avg</c>, <c>min</c> and
    /// <c>max</c> were accepted by <c>cordango check</c> and then landed as CORD2305 with the column
    /// left empty. That is a "not emitted yet", not a refusal, and this is it being emitted.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Ops =
        new HashSet<string>(StringComparer.Ordinal) { "sum", "count", "avg", "min", "max" };

    /// <summary>
    /// Comparisons a rollup's own filters may use.
    ///
    /// <para>Every operator the language's <c>filter</c> has except <c>overlaps</c>, which compares
    /// a row's own RANGE against a window and is a different shape from a comparison on one column.
    /// It was <c>eq</c> and <c>neq</c> alone for a while, which is how a perfectly ordinary
    /// "committed cost of everything approved, planned or active" — one <c>in</c> — landed as
    /// CORD2305 with the column left empty.</para>
    ///
    /// <para>What a filter cannot write must still stop the rollup rather than widen it: a predicate
    /// dropped is a bigger answer, not a smaller one. That rule is unchanged; the set it applies to
    /// is what grew.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Operators =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "eq", "neq", "gt", "gte", "lt", "lte",
            "contains", "in", "notIn", "between", "isEmpty", "isNotEmpty",
        };

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

        // Every other operation needs something to aggregate, and the child has to have it.
        if (child.Field(AppModel.Str(rollup["field"])) is not { } aggregated) return null;

        // `(decimal?)` inside the selector, so an EMPTY SET answers null rather than zero and the
        // narrowing below decides what that means. It matters more here than it did for sum alone:
        // the lowest of no rows is not zero, it is nobody's business, and a min that reported 0 for a
        // parent with no children would sort to the top of every list. Aggregating `long` would also
        // do integer arithmetic — the average of 3 and 4 is 3.5, not 3.
        var method = op switch
        {
            "sum" => "SumAsync",
            "avg" => "AverageAsync",
            "min" => "MinAsync",
            _ => "MaxAsync",
        };

        return Narrow(field, $"await {chain}.{method}(x => (decimal?)x.{aggregated.PropertyName}, ct)", counted: false);
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

    /// <summary>
    /// One leaf of a rollup's own filter, as the predicate that answers it.
    ///
    /// <para><b>Null takes the whole rollup with it</b>, which is the rule the caller relies on: a
    /// leaf quietly dropped is not a smaller answer, it is a bigger one. A committed cost that
    /// forgot "only approved" is the budget.</para>
    ///
    /// <para><b>The literal is typed from the COLUMN, not from the JSON.</b> This used to read the
    /// value with <c>AppModel.Str</c> and write it as a C# string whatever the field was, which had
    /// two failures nobody would have found from the outside. A filter on a number — a JSON number,
    /// which <c>Str</c> answers null for — emitted <c>x.Amount == null</c>: it compiled, it ran, and
    /// it counted the rows nobody had filled in. A filter on a multiselect emitted
    /// <c>x.Tags == "urgent"</c>, comparing a <c>List&lt;string&gt;</c> against a string, which did
    /// not compile at all. The first is the dangerous one, so the literal now comes from
    /// <see cref="FieldModel.ClrType"/> and a shape that cannot be written is refused.</para>
    /// </summary>
    private static string? Filter(JsonObject filter, EntityModel child)
    {
        // `path` is a hop through a reference — a join, not a comparison on this row. It is reported
        // on its own by the generator's filter walk, so it is refused here rather than guessed at.
        if (filter["path"] is not null) return null;

        if (child.Field(AppModel.Str(filter["field"])) is not { } field) return null;

        var op = AppModel.Str(filter["operator"]);
        if (op is null || !Operators.Contains(op)) return null;

        // A multiselect is a list and a json field is a document. Neither has a comparison that
        // survives the trip to SQL, and both used to emit code that did not compile.
        if (field.IsCollection || field.ClrType == "JsonDocument") return null;

        var column = $"x.{field.PropertyName}";

        // The two that take no value come first: demanding one before testing for them would make
        // them unusable.
        if (op is "isEmpty" or "isNotEmpty")
        {
            var empty = Empty(field, column);
            return Where(op == "isEmpty" ? empty : $"!({empty})");
        }

        if (op is "in" or "notIn")
        {
            var values = Literals(field, AppModel.Arr(filter["value"]));
            if (values is null) return null;

            // "in nothing" matches nothing and "not in nothing" matches everything. An OR over an
            // empty list would do the opposite of both, silently.
            if (values.Count == 0) return Where(op == "in" ? "false" : "true");

            var any = string.Join(" || ", values.Select(v => $"{column} == {v}"));
            return Where(op == "in" ? any : $"!({any})");
        }

        if (op == "between")
        {
            if (!Ordered(field)) return null;

            var bounds = Literals(field, AppModel.Arr(filter["value"]));
            if (bounds is not { Count: 2 }) return null;

            // Both ends included, written as one leaf because that is how the language spells it.
            return Where($"{column} >= {bounds[0]} && {column} <= {bounds[1]}");
        }

        if (op == "contains")
        {
            if (field.ClrType != "string") return null;
            if (Literal(field, filter["value"]) is not { } text) return null;

            // A null column is not a match, and Contains on one throws when the query runs rather
            // than when it is built.
            return Where($"{column} != null && {column}.Contains({text})");
        }

        if (op is not ("eq" or "neq") && !Ordered(field)) return null;
        if (Literal(field, filter["value"]) is not { } literal) return null;

        var comparison = op switch
        {
            "eq" => "==",
            "neq" => "!=",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            _ => "<=",
        };

        return Where($"{column} {comparison} {literal}");

        static string Where(string body) => $".Where(x => {body})";
    }

    /// <summary>Fields <c>gt</c>, <c>lt</c> and <c>between</c> can be written for. Text has no
    /// <c>&gt;</c> operator in C# and a boolean has no order worth having, so both are refused
    /// rather than emitted as something that does not compile.</summary>
    private static bool Ordered(FieldModel field) =>
        field.ClrType is "long" or "decimal" or "DateOnly" or "DateTimeOffset";

    /// <summary>
    /// Empty means null, and for text it also means the empty string — a field somebody cleared in a
    /// form and a field nobody ever filled are the same thing to the person asking.
    ///
    /// <para>A REQUIRED value column is never null, and testing one against null is a comparison the
    /// compiler warns is always false. The constant says the same thing without the warning, and
    /// says it honestly: nothing is missing here, so nothing matches.</para>
    /// </summary>
    private static string Empty(FieldModel field, string column) => field switch
    {
        { ClrType: "string" } => $"{column} == null || {column} == \"\"",
        { Required: true } => "false",
        _ => $"{column} == null",
    };

    /// <summary>Every value of a list operator, or null when any one of them cannot be written.
    /// Partial is not an option: three states in and one out is a filter that counts the wrong
    /// rows.</summary>
    private static List<string>? Literals(FieldModel field, JsonArray values)
    {
        var written = new List<string>();
        foreach (var value in values)
        {
            if (Literal(field, value) is not { } one) return null;
            written.Add(one);
        }

        return written;
    }

    /// <summary>
    /// A JSON value as a C# literal of the column's own type.
    ///
    /// <para>Null for anything that cannot be written, including a scope token: <c>{{today}}</c> and
    /// <c>{{actor.id}}</c> are resolved when a SCREEN renders, and a rollup is recomputed by a write
    /// hook where there is no screen, no actor and no request. Emitting the token as text would
    /// compare a status against the literal characters <c>{{actor.id}}</c> and match nothing, on
    /// every row, for ever.</para>
    /// </summary>
    private static string? Literal(FieldModel field, JsonNode? value)
    {
        if (value is not JsonValue leaf) return null;

        if (leaf.TryGetValue<string>(out var text))
        {
            if (text.Contains("{{", StringComparison.Ordinal)) return null;

            return field.ClrType switch
            {
                "string" => Naming.Literal(text),
                "long" => long.TryParse(text, CultureInfo.InvariantCulture, out var l)
                    ? l.ToString(CultureInfo.InvariantCulture) + "L"
                    : null,
                "decimal" => decimal.TryParse(text, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString(CultureInfo.InvariantCulture) + "m"
                    : null,
                "bool" => bool.TryParse(text, out var b) ? (b ? "true" : "false") : null,
                "DateOnly" => DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date)
                    ? $"new DateOnly({date.Year}, {date.Month}, {date.Day})"
                    : null,
                "DateTimeOffset" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, out var at)
                    // Fully qualified: this lands in a generated file whose usings are decided by
                    // what the ENTITY needs, and a rollup filter on a datetime must not depend on
                    // one of them happening to be there.
                    ? $"DateTimeOffset.Parse({Naming.Literal(at.ToString("O", CultureInfo.InvariantCulture))}, "
                        + "System.Globalization.CultureInfo.InvariantCulture)"
                    : null,
                _ => null,
            };
        }

        if (leaf.TryGetValue<bool>(out var flag))
            return field.ClrType == "bool" ? (flag ? "true" : "false") : null;

        // Read back through the JSON text rather than through TryGetValue<decimal>. A JsonValue
        // remembers the CLR type it was made from, so one built from an `int` answers false to
        // TryGetValue<decimal> while one PARSED from the same bytes answers true — and a definition
        // reaches this having been parsed while a test builds one by hand. Going through the text is
        // the same answer either way.
        if (!decimal.TryParse(leaf.ToJsonString(), CultureInfo.InvariantCulture, out var number))
            return null;

        return field.ClrType switch
        {
            "long" => decimal.Truncate(number) == number
                ? ((long)number).ToString(CultureInfo.InvariantCulture) + "L"
                : null,
            "decimal" => number.ToString(CultureInfo.InvariantCulture) + "m",
            // A number against text is the definition disagreeing with itself, not a conversion to
            // make quietly.
            _ => null,
        };
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
