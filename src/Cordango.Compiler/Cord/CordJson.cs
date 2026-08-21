// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>JSON mechanics the importer, the lowerer and the coverage report all need, in one place
/// so they cannot disagree about what a "node" is or how an overlay merges.</summary>
internal static class CordJson
{
    /// <summary>
    /// Removes <paramref name="key"/> from <paramref name="obj"/> and returns it as a string —
    /// but ONLY if it is one.
    ///
    /// <para>This is the importer's whole totality rule in one function. A property is modelled when
    /// it has the shape the model expects and is otherwise left exactly where it is, to be carried by
    /// the raw overlay. A definition whose <c>name</c> is a number (the historical fixtures have
    /// worse) therefore round-trips unchanged instead of being coerced, dropped, or throwing — and it
    /// costs a little coverage, which is the honest signal rather than a hidden one.</para>
    /// </summary>
    public static string? TakeString(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        obj.Remove(key);
        return s;
    }

    /// <summary>The same rule for a boolean. <c>false</c> is a value, not an absence — a field that
    /// says <c>"required": false</c> must come back saying it.</summary>
    public static bool? TakeBool(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v || !v.TryGetValue<bool>(out var b)) return null;
        obj.Remove(key);
        return b;
    }

    public static int? TakeInt(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v || !v.TryGetValue<int>(out var i)) return null;
        obj.Remove(key);
        return i;
    }

    /// <summary>Claims a value of any JSON type — a default may be a string, a number or a boolean.
    /// A JSON <c>null</c> is deliberately NOT claimed: it is indistinguishable from absence once it is
    /// in a nullable field, and guessing wrong would break the round-trip on a document nobody would
    /// think to test.</summary>
    public static JsonNode? TakeNode(JsonObject obj, string key)
    {
        if (!obj.ContainsKey(key) || obj[key] is not { } node) return null;
        var clone = node.DeepClone();
        obj.Remove(key);
        return clone;
    }

    /// <summary>Claims an object-valued property, handing back a mutable copy for the caller to strip
    /// as it models each part. Whatever is left over is that node's own raw overlay.</summary>
    public static JsonObject? TakeObject(JsonObject obj, string key)
    {
        if (obj[key] is not JsonObject o) return null;
        obj.Remove(key);
        return (JsonObject)o.DeepClone();
    }

    public static JsonArray? TakeArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray a) return null;
        obj.Remove(key);
        return (JsonArray)a.DeepClone();
    }

    /// <summary>Null when the object is empty, so an exhausted overlay disappears instead of lowering
    /// as <c>{}</c> and breaking the round-trip.</summary>
    public static JsonObject? Remainder(JsonObject obj) => obj.Count > 0 ? obj : null;

    /// <summary>
    /// Deep-merges <paramref name="overlay"/> onto <paramref name="target"/>, overlay winning.
    ///
    /// <para>Objects merge key by key; anything else REPLACES. An array is replaced rather than
    /// concatenated or merged element-wise, because a definition's arrays are ordered and identified
    /// positionally — merging <c>entities</c> by index would silently marry entity 3 of one document
    /// to entity 3 of another.</para>
    /// </summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            if (target[key] is JsonObject existing && value is JsonObject sub)
            {
                Merge(existing, sub);
                continue;
            }
            target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// How many nodes a subtree holds: itself, plus everything under it.
    ///
    /// <para>The unit of <see cref="CordCoverage"/>. Containers count as one alongside their
    /// children so that a deeply nested block tree weighs what it costs to author, and a definition's
    /// total is dominated by the parts that are actually large.</para>
    /// </summary>
    public static int Nodes(JsonNode? node) => node switch
    {
        JsonObject o => 1 + o.Sum(p => Nodes(p.Value)),
        JsonArray a => 1 + a.Sum(Nodes),
        // Null is NOTHING, not one thing. A section an app does not have resolves to null, and
        // counting that as a node made every app without `processes` contribute a phantom covered
        // node — so a section nobody has modelled reported above 0%. (A JSON `null` VALUE is also
        // null here and is likewise not counted; the two are indistinguishable in JsonNode, and for
        // a size metric neither is worth a point.)
        null => 0,
        _ => 1,
    };

    /// <summary>RFC 6901 escaping. Rare in practice — definition keys are identifiers — but a
    /// pointer that silently means something else is worse than a rare one.</summary>
    public static string Pointer(string parent, string key) =>
        parent + "/" + key.Replace("~", "~0").Replace("/", "~1");
}
