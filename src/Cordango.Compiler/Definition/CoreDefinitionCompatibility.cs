// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>What replaying one shipped core definition against a later one found.</summary>
/// <param name="Breaking">Changes that would corrupt or orphan data in a tenant that already ran the
/// earlier version. Any entry fails the build.</param>
/// <param name="Review">Changes that are legal but alter behaviour for existing rows — surfaced so a
/// human decides, rather than silently shipping.</param>
public sealed record CompatibilityReport(IReadOnlyList<string> Breaking, IReadOnlyList<string> Review)
{
    public bool Ok => Breaking.Count == 0;
}

/// <summary>
/// Checks that a core app's definition only ever grew.
///
/// This exists because the data plane cannot absorb anything else.
/// <see cref="Runtime"/>'s provisioner is additive-only: it adds columns and never drops or retypes
/// one, and it swallows a failed index into a warning. So a type change does not degrade — the
/// manifest starts claiming a column is numeric while Postgres still holds text, and EVERY read of
/// that table throws. A renamed entity key does not migrate — it creates a fresh empty table and
/// abandons the rows. A late `unique` does not apply — it fails quietly and the guarantee never
/// exists. None of these surface at deploy time; they surface as a broken tenant.
///
/// The check runs from EVERY previously shipped version to the current one, not just the last, which
/// is why the registry keeps them all. Comparing only against the immediately preceding version
/// would let a field be removed in v2 and re-added with a different type in v3 — each step locally
/// fine, the v1→v3 path fatal.
/// </summary>
public static class CoreDefinitionCompatibility
{
    public static CompatibilityReport Check(JsonNode previous, JsonNode current)
    {
        var breaking = new List<string>();
        var review = new List<string>();

        // The app key feeds the handle and, through it, the generated table names.
        var prevKey = Str(previous, "key");
        var curKey = Str(current, "key");
        if (prevKey != curKey)
            breaking.Add($"app key changed '{prevKey}' -> '{curKey}' (the generated storage names hang off it)");

        var prevEntities = EntitiesOf(previous);
        var curEntities = EntitiesOf(current);

        foreach (var (ekey, prevEntity) in prevEntities)
        {
            if (!curEntities.TryGetValue(ekey, out var curEntity))
            {
                breaking.Add($"entity '{ekey}' was removed or renamed (its table and every reference to it are orphaned)");
                continue;
            }

            var prevFields = FieldsOf(prevEntity);
            var curFields = FieldsOf(curEntity);
            foreach (var (fkey, prevField) in prevFields)
            {
                if (!curFields.TryGetValue(fkey, out var curField))
                {
                    breaking.Add($"field '{ekey}.{fkey}' was removed or renamed (its column is abandoned, not migrated)");
                    continue;
                }
                CheckField(ekey, fkey, prevField, curField, breaking, review);
            }
        }

        return new CompatibilityReport(breaking, review);
    }

    private static void CheckField(string ekey, string fkey, JsonObject prev, JsonObject cur,
        List<string> breaking, List<string> review)
    {
        var at = $"field '{ekey}.{fkey}'";

        var prevType = Str(prev, "type");
        var curType = Str(cur, "type");
        if (prevType != curType)
            breaking.Add($"{at} type changed '{prevType}' -> '{curType}' (the column is never retyped, so every read of the table throws)");

        // A reference that starts pointing somewhere else leaves the stored ids pointing at rows of
        // the wrong entity — they still LOOK like valid references.
        if (Str(prev, "targetEntity") is { } pte && Str(cur, "targetEntity") is { } cte && pte != cte)
            breaking.Add($"{at} targetEntity changed '{pte}' -> '{cte}' (stored ids now point at the wrong entity)");
        if (Str(prev, "targetApp") != Str(cur, "targetApp"))
            breaking.Add($"{at} targetApp changed '{Str(prev, "targetApp") ?? "(local)"}' -> '{Str(cur, "targetApp") ?? "(local)"}'");
        if (Str(prev, "onDelete") != Str(cur, "onDelete"))
            breaking.Add($"{at} onDelete changed '{Str(prev, "onDelete") ?? "(none)"}' -> '{Str(cur, "onDelete") ?? "(none)"}' (existing rows were written under the old rule)");

        // Tightening a constraint on data that already exists: rows written before the change do not
        // satisfy it, and the provisioner's index attempt fails into a log line nobody reads.
        if (!Bool(prev, "required") && Bool(cur, "required"))
            breaking.Add($"{at} became required (rows already written without it cannot satisfy it)");
        if (!Bool(prev, "unique") && Bool(cur, "unique"))
            breaking.Add($"{at} became unique (the index is created best-effort and silently does not apply to existing duplicates)");

        // Dropping an option strands every row already holding that value: it renders as a raw code
        // with no label and no colour, and filters stop offering it.
        var prevOptions = OptionValues(prev);
        var curOptions = OptionValues(cur);
        foreach (var gone in prevOptions.Where(v => !curOptions.Contains(v)))
            breaking.Add($"{at} option '{gone}' was removed or renamed (rows holding it become unlabelled)");

        // Legal, but it changes what existing rows MEAN — a human decides.
        if (Json(prev, "computed") != Json(cur, "computed"))
            review.Add($"{at} computed expression changed — existing rows keep their old values until rewritten");
        if (Json(prev, "default") != Json(cur, "default"))
            review.Add($"{at} default changed — applies only to rows created from now on");
    }

    private static Dictionary<string, JsonObject> EntitiesOf(JsonNode def) =>
        ByKey(def["entities"] as JsonArray);

    private static Dictionary<string, JsonObject> FieldsOf(JsonObject entity) =>
        ByKey(entity["fields"] as JsonArray);

    private static Dictionary<string, JsonObject> ByKey(JsonArray? items)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var n in items ?? new JsonArray())
            if (n is JsonObject o && Str(o, "key") is { Length: > 0 } k)
                map[k] = o;
        return map;
    }

    private static IReadOnlyCollection<string> OptionValues(JsonObject field)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in field["options"] as JsonArray ?? new JsonArray())
        {
            // Options are objects ({value,label,color}) but a bare string is also valid shorthand.
            var v = n is JsonObject o ? Str(o, "value") : n?.GetValue<string>();
            if (v is { Length: > 0 }) values.Add(v);
        }
        return values;
    }

    private static string? Str(JsonNode? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonNode? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string? Json(JsonNode? o, string key) => o?[key]?.ToJsonString();
}
