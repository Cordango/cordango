// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// What one lowering produced, and where each part of it came from.
/// </summary>
/// <param name="Definition">The App Definition. Handed straight to the existing validation pipeline —
/// Cord adds no validator of its own and no second notion of a correct application.</param>
/// <param name="SemanticPointers">JSON Pointers the model emitted from its own structure.</param>
/// <param name="RawPointers">JSON Pointers at the ROOT of each raw overlay fragment. Everything at or
/// under one of these was carried, not modelled — which is what <see cref="CordCoverage"/> counts and
/// what the raw allowlist pins.</param>
public sealed record CordLowering(
    JsonNode Definition,
    IReadOnlyList<string> SemanticPointers,
    IReadOnlyList<string> RawPointers);

/// <summary>
/// <see cref="CordApp"/> → App Definition. Pure, deterministic, and a function of the model ALONE.
///
/// <para><b>Rule 2.</b> Lowering must never read the definition it was imported from, consult a
/// database, or reconstruct a fact from anywhere but the CordApp in front of it. Today the App
/// Definition is the stored artifact and Cord is derived from it, which is the right migration
/// decision — but the eventual open-source story wants <c>*.cord</c> to stand on its own as source
/// with the App Definition demoted to a compiled deployment artifact. The moment lowering needs
/// something the model does not carry, that future quietly closes. If lowering needs a fact, the fact
/// belongs in the model.</para>
///
/// <para><c>via</c> looks like an exception and is not: it is derived from the model's OWN entities
/// via <see cref="CordRefIndex.FromModel"/>, never from the source document.</para>
/// </summary>
public static class CordLower
{
    public static JsonNode Lower(CordApp app) => Lowering(app).Definition;

    public static CordLowering Lowering(CordApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var doc = new JsonObject();
        var semantic = new List<string>();
        var raw = new List<string>();
        var refs = CordRefIndex.FromModel(app.EntityList);

        // (entity, field) pairs a lifecycle governs. Computed before anything is emitted because the
        // entities are lowered first and need to know.
        var governed = app.ProcessList
            .Where(p => p.Entity is not null && p.StateField is not null)
            .Select(p => (p.Entity, p.StateField))
            .ToHashSet();

        // …and WHICH state a governed field starts in, which is the other thing the lifecycle already
        // says about it. See the `default` note below.
        var initialStates = app.ProcessList
            .Where(p => p.Entity is not null && p.StateField is not null && p.InitialState is not null)
            .ToDictionary(p => (p.Entity, p.StateField), p => p.InitialState!);

        void Put(string pointer, string key, JsonNode? value, JsonObject into)
        {
            if (value is null) return;
            into[key] = value;
            semantic.Add(CordJson.Pointer(pointer, key));
        }

        void Overlay(string pointer, JsonObject? overlay, JsonObject into)
        {
            if (overlay is null) return;
            // The overlay's ROOTS are the raw pointers. Recording every node underneath would say the
            // same thing at a hundred times the size, and coverage counts the subtree anyway.
            foreach (var (key, _) in overlay) raw.Add(CordJson.Pointer(pointer, key));
            CordJson.Merge(into, overlay);
        }

        // Emitted in CordApp.Modelled order throughout. Key order is not load-bearing for equality —
        // DefinitionHash.Canonical sorts ordinally — but a deterministic lowerer is far easier to diff
        // by eye when a round-trip does fail.
        Put("", "key", app.Key, doc);
        Put("", "name", app.Name, doc);
        Put("", "version", app.Version, doc);
        Put("", "description", app.Description, doc);
        Put("", "schemaVersion", app.SchemaVersion, doc);

        if (app.Entities is { } entities)
        {
            var arr = new JsonArray();
            semantic.Add("/entities");
            for (var i = 0; i < entities.Count; i++)
                arr.Add(Entity(entities[i], $"/entities/{i}", refs));
            doc["entities"] = arr;
        }

        // Screens authored by Cord, or raised from an imported shape Cord can reproduce exactly.
        // Unsupported imported page shapes stay in the overlay and leave Screens null, preserving
        // the total round trip without pretending they are independently editable.
        if (app.Screens is { Count: > 0 } screens)
        {
            var (pages, views) = CordLowerScreens.Emit(screens);
            semantic.Add("/pages");
            semantic.Add("/views");
            doc["pages"] = pages;
            doc["views"] = views;
        }

        CordLowerBehaviour.Emit(app, doc, semantic, raw);

        Overlay("", app.Raw, doc);
        return new CordLowering(doc, semantic, raw);

        JsonObject Entity(CordEntity e, string at, CordRefIndex index)
        {
            var o = new JsonObject();
            Put(at, "key", e.Key, o);
            Put(at, "label", e.Label, o);
            Put(at, "labelPlural", e.LabelPlural, o);
            Put(at, "description", e.Description, o);
            Put(at, "icon", e.Icon, o);
            Put(at, "displayField", e.DisplayField, o);
            Put(at, "imageField", e.ImageField, o);
            Put(at, "role", e.Role, o);
            Put(at, "kind", e.Kind, o);

            if (e.Owner is { } owner)
            {
                var node = new JsonObject { ["parent"] = owner.Parent };
                // Derived from the model's own entities, never from the source document (rule 2).
                var via = owner.Via ?? (e.Key is { } k ? index.SoleReferenceTo(k, owner.Parent) : null);
                if (via is not null) node["via"] = via;
                if (owner.As is { } asKind) node["as"] = asKind;
                Put(at, "ownedBy", node, o);
            }

            if (e.Series is { } series)
                Put(at, "series", new JsonObject
                {
                    ["partition"] = series.Partition,
                    ["order"] = series.Order,
                }, o);

            if (e.Fields is { } fields)
            {
                var arr2 = new JsonArray();
                semantic.Add(at + "/fields");
                for (var i = 0; i < fields.Count; i++)
                    arr2.Add(Field(fields[i], $"{at}/fields/{i}", e.Key, index));
                o["fields"] = arr2;
            }

            if (e.Unique is { } unique)
            {
                var arr2 = new JsonArray();
                foreach (var group in unique)
                    arr2.Add(new JsonArray(group.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()));
                Put(at, "unique", arr2, o);
            }

            if (e.Calendar is { } calendar) Put(at, "calendar", calendar, o);

            Overlay(at, e.Raw, o);
            return o;
        }

        JsonObject Field(CordField f, string at, string? entityKey, CordRefIndex index)
        {
            var o = new JsonObject();
            Put(at, "key", f.Key, o);
            Put(at, "label", f.Label, o);
            Put(at, "type", f.Type, o);
            if (f.Required is { } req) Put(at, "required", req, o);
            if (f.Unique is { } uniq) Put(at, "unique", uniq, o);
            if (f.Indexed is { } idx) Put(at, "indexed", idx, o);
            Put(at, "help", f.Help, o);
            Put(at, "group", f.Group, o);
            // A GOVERNED field's default is the lifecycle's `initialState`, said twice. The gate's own
            // words: "process governs 'x.status', so 'initialState' IS that field's default — remove
            // the field's 'default'". An author who states both has said one true thing in two places,
            // which is a reasonable thing to do and produced a rejected document (smoke run
            // 2026-08-11). Deduplicating is the same mechanical work as governed OPTIONS above, and it
            // is only safe to do SILENTLY when the two agree — a default that CONTRADICTS the initial
            // state is two different claims, and picking one would be Cord deciding something the
            // author did not. CordCheck refuses that case in the author's own terms.
            var redundantDefault = f.Key is not null
                && initialStates.TryGetValue((entityKey, f.Key), out var initial)
                && f.Default is JsonValue dv && dv.TryGetValue<string>(out var dt) && dt == initial;
            if (!redundantDefault) Put(at, "default", f.Default?.DeepClone(), o);
            if (f.Precision is { } precision) Put(at, "precision", precision, o);
            if (f.Scale is { } scale) Put(at, "scale", scale, o);
            Put(at, "unit", f.Unit, o);
            Put(at, "prefix", f.Prefix, o);
            Put(at, "input", f.Input, o);
            Put(at, "currency", f.Currency, o);
            Put(at, "role", f.Role, o);
            Put(at, "targetEntity", f.TargetEntity, o);
            Put(at, "targetApp", f.TargetApp, o);
            Put(at, "onDelete", f.OnDelete, o);

            // A field GOVERNED by a lifecycle must not carry options: the gate's own words are
            // "its states ARE that field's options". An author who describes a claim's states in the
            // lifecycle AND lists them on the select has said one true thing twice, which is a
            // reasonable thing to do and produced a rejected document. Deduplicating is exactly the
            // mechanical work this layer exists to absorb — the states win, because that is what the
            // platform means by governed.
            //
            // Safe for the round trip: a stored document cannot contain both (the gate forbids it), so
            // an imported field never reaches here with options to drop.
            if (f.Options is { } options && !governed.Contains((entityKey, f.Key)))
            {
                var arr2 = new JsonArray();
                foreach (var opt in options)
                {
                    var node = new JsonObject { ["value"] = opt.Value, ["label"] = opt.Label };
                    if (opt.Color is { } color) node["color"] = color;
                    if (opt.Phase is { } phase) node["phase"] = phase;
                    if (opt.Raw is { } extra) CordJson.Merge(node, extra);
                    arr2.Add(node);
                }
                Put(at, "options", arr2, o);
            }

            if (f.Calc is { } calc) Put(at, "computed", Computed(calc, entityKey, index), o);

            Overlay(at, f.Raw, o);
            return o;
        }
    }

    /// <summary><c>hiring_line</c> → <c>Hiring line</c>. A label the author did not bother to give,
    /// rather than a design decision.</summary>
    private static string Title(string key) =>
        key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    /// <summary>
    /// The semantic aggregate → the <c>rollup</c> machinery.
    ///
    /// <para>This is the whole point of the layer in one method: <c>via</c> is derived rather than
    /// authored, <c>match</c> is implied by <see cref="CordAggregate.Over"/> naming a reference
    /// instead of <c>"mine"</c>, and the window's direction is decided by which record each field
    /// belongs to rather than by remembering which of two spellings to use.</para>
    /// </summary>
    private static JsonObject Computed(CordCalc calc, string? entityKey, CordRefIndex refs)
    {
        if (calc is CordExpr e) return new JsonObject { ["expr"] = e.Expr };

        var agg = (CordAggregate)calc;
        var rollup = new JsonObject { ["entity"] = agg.Of };

        // An explicit Via wins; otherwise derive it. A null here means the model is not answerable —
        // CordCheck refuses it in Cord's own terms rather than letting the gate complain about a
        // property the author never wrote.
        var via = agg.Via ?? (entityKey is not null ? refs.Infer(entityKey, agg.Of, agg.Over) : null);
        if (via is not null) rollup["via"] = via;

        if (agg.Over != CordAggregate.Mine) rollup["match"] = agg.Over;
        rollup["op"] = agg.Op;
        if (agg.Field is { } field) rollup["field"] = field;

        rollup["window"] = agg.During switch
        {
            CordCovering c => Covering(c),
            CordInside i => Inside(i),
            _ => null,
        };
        if (rollup["window"] is null) rollup.Remove("window");

        if (agg.Where is { Count: > 0 } filters)
        {
            var arr = new JsonArray();
            foreach (var f in filters)
            {
                var node = new JsonObject();
                if (f.Field is { } key) node["field"] = key;
                if (f.Operator is { } op) node["operator"] = op;
                if (f.Value is { } value) node["value"] = value.DeepClone();
                if (f.Raw is { } extra) CordJson.Merge(node, extra);
                arr.Add(node);
            }
            rollup["filters"] = arr;
        }

        return new JsonObject { ["rollup"] = rollup };

        static JsonObject Covering(CordCovering c)
        {
            var w = new JsonObject { ["from"] = c.From };
            if (c.To is { } to) w["to"] = to;
            w["against"] = c.MyPoint;
            return w;
        }

        static JsonObject Inside(CordInside i)
        {
            var within = new JsonObject { ["from"] = i.MyFrom };
            if (i.MyTo is { } to) within["to"] = to;
            return new JsonObject { ["at"] = i.At, ["within"] = within };
        }
    }
}
