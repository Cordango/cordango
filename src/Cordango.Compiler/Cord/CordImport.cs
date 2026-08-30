// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// App Definition → <see cref="CordApp"/>. <b>Total: it never fails and never throws.</b>
///
/// <para>There is no error list and no "unsupported" outcome, on purpose. Import has to work on
/// documents nobody here wrote — apps edited in the WYSIWYG editor, apps refined by an older model,
/// the historical fixtures that do not pass today's gate at all — because the alternative is an
/// authoring layer that can only touch applications it created. Anything the model does not represent
/// is carried by the raw overlay, so the worst case is low coverage rather than a refusal.</para>
///
/// <para>That totality is also what makes the round-trip property meaningful. If import could decline
/// a document, <c>Lower(Import(x)) == x</c> would only be asserted over the documents import happened
/// to like.</para>
///
/// <para><b>The rule, everywhere:</b> a property is claimed only when it has the SHAPE the model
/// expects, and is otherwise left where it is for the overlay to carry. A definition whose
/// <c>name</c> is a number round-trips unchanged instead of being coerced, dropped, or throwing — and
/// it costs a little coverage, which is the honest signal rather than a hidden one.</para>
/// </summary>
public static class CordImport
{
    /// <param name="definition">Any App Definition. Not mutated — the caller's node is cloned first,
    /// because everything below removes properties as it claims them.</param>
    public static CordApp Import(JsonNode? definition)
    {
        // A non-object is not a definition, but it is also not a reason to throw: the empty model
        // lowers back to nothing, which is exactly as much as was there.
        if (definition is not JsonObject src) return new CordApp();

        // Built from the ORIGINAL document, before anything is claimed: `via` inference needs to see
        // every entity's reference fields, including those of entities not yet imported.
        var refs = CordRefIndex.FromDefinition(src);

        var rest = (JsonObject)src.DeepClone();

        var key = CordJson.TakeString(rest, "key");
        var name = CordJson.TakeString(rest, "name");
        var version = CordJson.TakeString(rest, "version");
        var description = CordJson.TakeString(rest, "description");
        var schemaVersion = CordJson.TakeString(rest, "schemaVersion");

        List<CordEntity>? entities = null;
        if (CordJson.TakeArray(rest, "entities") is { } arr)
        {
            // All-or-nothing at the ARRAY level: a single malformed element would otherwise have to be
            // represented as a hole in a list, and a list with holes cannot round-trip positionally.
            if (arr.All(x => x is JsonObject))
                entities = arr.Select(x => Entity((JsonObject)x!, refs)).ToList();
            else
                rest["entities"] = arr;
        }

        var (processes, actions, schedules, roles) = CordImportBehaviour.Take(rest);
        var screens = CordImportScreens.Take(rest);

        return new CordApp(key, name, version, description, schemaVersion, entities,
            Screens: screens,
            Processes: processes, Actions: actions, Schedules: schedules, Roles: roles,
            Raw: CordJson.Remainder(rest));
    }

    private static CordEntity Entity(JsonObject src, CordRefIndex refs)
    {
        var rest = (JsonObject)src.DeepClone();
        var key = CordJson.TakeString(rest, "key");

        var owner = Ownership(rest, key, refs);
        var series = Series(rest);

        List<CordField>? fields = null;
        if (CordJson.TakeArray(rest, "fields") is { } arr)
        {
            if (arr.All(x => x is JsonObject))
                fields = arr.Select(x => Field((JsonObject)x!, key, refs)).ToList();
            else
                rest["fields"] = arr;
        }

        return new CordEntity(
            key,
            CordJson.TakeString(rest, "label"),
            CordJson.TakeString(rest, "labelPlural"),
            CordJson.TakeString(rest, "description"),
            CordJson.TakeString(rest, "icon"),
            CordJson.TakeString(rest, "displayField"),
            CordJson.TakeString(rest, "imageField"),
            CordJson.TakeString(rest, "role"),
            CordJson.TakeString(rest, "kind"),
            owner,
            series,
            fields,
            Unique(rest),
            // Only the bare flag is modelled. A definition carrying the OBJECT form has named fields
            // the semantic surface deliberately does not ask for, so TakeBool leaves it where it is
            // and it round-trips through Raw — carried exactly, rather than modelled half-right.
            CordJson.TakeBool(rest, "calendar"),
            CordJson.Remainder(rest));
    }

    private static CordOwnership? Ownership(JsonObject rest, string? entityKey, CordRefIndex refs)
    {
        if (rest["ownedBy"] is not JsonObject o) return null;
        var copy = (JsonObject)o.DeepClone();
        var parent = CordJson.TakeString(copy, "parent");
        var via = CordJson.TakeString(copy, "via");
        var role = CordJson.TakeString(copy, "as");
        // The shape is closed in the schema, so anything left over means this is not the construct we
        // think it is — carry the whole thing rather than model it half-right.
        if (parent is null || via is null || copy.Count > 0) return null;
        rest.Remove("ownedBy");

        // Same rule as a rollup's join: keep it only where inference would not reproduce it.
        var inferred = entityKey is not null ? refs.SoleReferenceTo(entityKey, parent) : null;
        return new CordOwnership(parent, inferred == via ? null : via, role);
    }

    private static CordSeries? Series(JsonObject rest)
    {
        if (rest["series"] is not JsonObject o) return null;
        var copy = (JsonObject)o.DeepClone();
        var partition = CordJson.TakeString(copy, "partition");
        var order = CordJson.TakeString(copy, "order");
        if (partition is null || order is null || copy.Count > 0) return null;
        rest.Remove("series");
        return new CordSeries(partition, order);
    }

    private static IReadOnlyList<IReadOnlyList<string>>? Unique(JsonObject rest)
    {
        if (rest["unique"] is not JsonArray arr) return null;
        var groups = new List<IReadOnlyList<string>>();
        foreach (var group in arr)
        {
            if (group is not JsonArray g) return null;
            var keys = new List<string>();
            foreach (var k in g)
            {
                if (k is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
                keys.Add(s);
            }
            groups.Add(keys);
        }
        rest.Remove("unique");
        return groups;
    }

    private static CordField Field(JsonObject src, string? entityKey, CordRefIndex refs)
    {
        var rest = (JsonObject)src.DeepClone();
        var key = CordJson.TakeString(rest, "key");

        return new CordField(
            key,
            CordJson.TakeString(rest, "label"),
            CordJson.TakeString(rest, "type"),
            CordJson.TakeBool(rest, "required"),
            CordJson.TakeBool(rest, "unique"),
            CordJson.TakeBool(rest, "indexed"),
            CordJson.TakeString(rest, "help"),
            CordJson.TakeString(rest, "group"),
            CordJson.TakeNode(rest, "default"),
            CordJson.TakeInt(rest, "precision"),
            CordJson.TakeInt(rest, "scale"),
            CordJson.TakeString(rest, "unit"),
            CordJson.TakeString(rest, "prefix"),
            CordJson.TakeString(rest, "input"),
            CordJson.TakeString(rest, "currency"),
            CordJson.TakeString(rest, "role"),
            CordJson.TakeString(rest, "targetEntity"),
            CordJson.TakeString(rest, "targetApp"),
            CordJson.TakeString(rest, "onDelete"),
            Options(rest),
            Calc(rest, entityKey, refs),
            CordJson.Remainder(rest));
    }

    private static IReadOnlyList<CordOption>? Options(JsonObject rest)
    {
        if (rest["options"] is not JsonArray arr) return null;
        var options = new List<CordOption>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) return null;
            var copy = (JsonObject)o.DeepClone();
            var value = CordJson.TakeString(copy, "value");
            var label = CordJson.TakeString(copy, "label");
            if (value is null || label is null) return null;
            options.Add(new CordOption(value, label,
                CordJson.TakeString(copy, "color"),
                CordJson.TakeString(copy, "phase"),
                CordJson.Remainder(copy)));
        }
        rest.Remove("options");
        return options;
    }

    /// <summary>
    /// <c>computed</c> → <see cref="CordCalc"/>, which is where the mechanical detail goes away.
    /// </summary>
    private static CordCalc? Calc(JsonObject rest, string? entityKey, CordRefIndex refs)
    {
        if (rest["computed"] is not JsonObject computed) return null;
        var copy = (JsonObject)computed.DeepClone();

        if (copy["expr"] is JsonValue ev && ev.TryGetValue<string>(out var expr))
        {
            copy.Remove("expr");
            if (copy.Count > 0) return null;   // expr AND rollup, or something unrecognised
            rest.Remove("computed");
            return new CordExpr(expr);
        }

        if (copy["rollup"] is not JsonObject rollupNode) return null;
        var rollup = (JsonObject)rollupNode.DeepClone();
        copy.Remove("rollup");
        if (copy.Count > 0) return null;

        var op = CordJson.TakeString(rollup, "op");
        var of = CordJson.TakeString(rollup, "entity");
        var via = CordJson.TakeString(rollup, "via");
        var field = CordJson.TakeString(rollup, "field");
        var match = CordJson.TakeString(rollup, "match");
        if (op is null || of is null || via is null) return null;

        // `match` present means a SIBLING aggregation — both records point at a shared parent — and
        // the field it names is exactly what the author would say they are aggregating over.
        var over = match ?? CordAggregate.Mine;

        var during = Window(rollup);
        if (rollup["window"] is not null) return null;   // a window we could not read: carry it all
        var where = Filters(rollup);
        if (rollup["filters"] is not null) return null;

        if (rollup.Count > 0) return null;                // an unrecognised rollup property

        // Keep `via` ONLY where inference would not reproduce it. Anything else would either break the
        // round-trip (dropping a via inference disagrees with) or overstate coverage (recording a via
        // that was never actually inferred).
        var inferred = entityKey is not null ? refs.Infer(entityKey, of, over) : null;
        var explicitVia = inferred == via ? null : via;

        rest.Remove("computed");
        return new CordAggregate(op, of, field, over, explicitVia, during, where);
    }

    /// <summary>Reads whichever window direction is present, and REMOVES it only on success — the
    /// caller checks whether <c>window</c> is still there and falls back to raw if so.</summary>
    private static CordWindow? Window(JsonObject rollup)
    {
        if (rollup["window"] is not JsonObject w) return null;
        var copy = (JsonObject)w.DeepClone();

        var at = CordJson.TakeString(copy, "at");
        if (at is not null)
        {
            if (CordJson.TakeObject(copy, "within") is not { } within) return null;
            var from = CordJson.TakeString(within, "from");
            var to = CordJson.TakeString(within, "to");
            if (from is null || within.Count > 0 || copy.Count > 0) return null;
            rollup.Remove("window");
            return new CordInside(at, from, to);
        }

        var spanFrom = CordJson.TakeString(copy, "from");
        var spanTo = CordJson.TakeString(copy, "to");
        var against = CordJson.TakeString(copy, "against");
        if (spanFrom is null || against is null || copy.Count > 0) return null;
        rollup.Remove("window");
        return new CordCovering(spanFrom, spanTo, against);
    }

    private static IReadOnlyList<CordFilter>? Filters(JsonObject rollup)
    {
        if (rollup["filters"] is not JsonArray arr) return null;
        var filters = new List<CordFilter>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) return null;
            var copy = (JsonObject)o.DeepClone();
            filters.Add(new CordFilter(
                CordJson.TakeString(copy, "field"),
                CordJson.TakeString(copy, "operator"),
                CordJson.TakeNode(copy, "value"),
                CordJson.Remainder(copy)));
        }
        rollup.Remove("filters");
        return filters;
    }
}
