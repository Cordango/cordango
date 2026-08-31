// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Applies a behavior-pass document onto a validated PURE-DOMAIN definition. The behavior pass
/// may only author the behavior plane — processes, commands, workflows, roles — plus the
/// authoring-only `entityPatches` (each process's status field, and the calendar guard that reads
/// it), which this merge LOWERS into the entity and never stores. Ordinary entities/fields/
/// relations are never copied, so the behavior model is structurally incapable of corrupting the
/// data model no matter what it emits (the DesignMerge principle, applied to the verbs).
/// </summary>
public static class BehaviorMerge
{
    /// <summary>Version of the behavior authoring contract. The tool schema pins it as a const
    /// and the merge rejects a mismatch, so an emission produced against an older contract can
    /// never be silently lowered by a newer merge. 1.1 added the `calendarHideWhen` patch channel
    /// and stopped requiring `statusField` on every entry.</summary>
    public const string AuthoringVersion = "1.1";

    /// <summary>Top-level behavior keys copied wholesale onto the definition.</summary>
    private static readonly string[] TopLevelKeys = ["processes", "commands", "workflows", "roles"];

    /// <summary>Merge <paramref name="behaviorDoc"/> into a deep clone of
    /// <paramref name="domainDef"/>. <paramref name="issues"/> collects what the merge had to
    /// reject (unknown patch entities, a process governing a missing field) with repair-guiding
    /// messages; callers concatenate them with the gate's errors for the repair loop.</summary>
    public static JsonNode Apply(JsonNode domainDef, JsonNode? behaviorDoc, out List<string> issues)
    {
        issues = new List<string>();
        var merged = domainDef.DeepClone().AsObject();
        if (behaviorDoc is not JsonObject behavior)
        {
            issues.Add("BEHAVIOR: the behavior document is not an object");
            return merged;
        }
        if (behavior["authoringVersion"]?.GetValue<string>() is not AuthoringVersion)
            issues.Add($"BEHAVIOR: authoringVersion must be \"{AuthoringVersion}\"");

        foreach (var key in TopLevelKeys)
            if (behavior[key] is { } v)
                merged[key] = v.DeepClone();

        ApplyEntityPatches(merged, behavior["entityPatches"], issues);
        NormalizeProcessStatusFields(merged, issues);
        return merged;
    }

    /// <summary>Lower each `entityPatches` entry into its entity: create the status field as a
    /// role:'status' select, or — when the domain pass modeled the field anyway — normalize the
    /// existing select to process governance, and hang `calendarHideWhen` off the entity's
    /// calendar opt-in. The patch list itself is never stored.</summary>
    private static void ApplyEntityPatches(JsonObject merged, JsonNode? patches, List<string> issues)
    {
        if (patches is null) return;
        if (patches is not JsonArray arr)
        {
            issues.Add("BEHAVIOR: 'entityPatches' must be an array of "
                + "{ entity, statusField?, calendarHideWhen? } objects");
            return;
        }
        var entities = merged["entities"] as JsonArray ?? new JsonArray();
        var seen = new HashSet<string>();
        var calendarSeen = new HashSet<string>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o || o["entity"]?.GetValue<string>() is not { } ekey)
            {
                issues.Add("BEHAVIOR: every entityPatches entry needs 'entity'");
                continue;
            }
            var ent = entities.OfType<JsonObject>().FirstOrDefault(e => e["key"]?.GetValue<string>() == ekey);
            if (ent is null)
            {
                issues.Add($"BEHAVIOR: entityPatches references unknown entity '{ekey}'");
                continue;
            }
            if (o["calendarHideWhen"] is { } hideWhen)
            {
                if (!calendarSeen.Add(ekey))
                    issues.Add($"BEHAVIOR: duplicate entityPatches 'calendarHideWhen' for '{ekey}'");
                else
                    ApplyCalendarHideWhen(ent, ekey, hideWhen, issues);
            }
            if (o["statusField"] is null && o["calendarHideWhen"] is null)
            {
                issues.Add($"BEHAVIOR: entityPatches entry for '{ekey}' declares neither "
                    + "'statusField' nor 'calendarHideWhen' — drop it or say what it patches");
                continue;
            }
            if (o["statusField"] is null) continue;
            if (o["statusField"] is not JsonObject sf || sf["key"]?.GetValue<string>() is not { } fkey)
            {
                issues.Add($"BEHAVIOR: the entityPatches 'statusField' for '{ekey}' needs a 'key'");
                continue;
            }
            if (!seen.Add($"{ekey}.{fkey}"))
            {
                issues.Add($"BEHAVIOR: duplicate entityPatches entry for '{ekey}.{fkey}'");
                continue;
            }
            var fields = ent["fields"] as JsonArray;
            if (fields is null) ent["fields"] = fields = new JsonArray();
            var existing = fields.OfType<JsonObject>().FirstOrDefault(f => f["key"]?.GetValue<string>() == fkey);
            if (existing is null)
            {
                var field = new JsonObject
                {
                    ["key"] = fkey,
                    ["label"] = sf["label"]?.GetValue<string>() ?? fkey,
                    ["type"] = "select",
                    ["role"] = "status",
                };
                if (sf["group"]?.GetValue<string>() is { } group) field["group"] = group;
                fields.Add(field);
            }
            else if (existing["type"]?.GetValue<string>() == "select")
            {
                existing["role"] = "status";
                if (sf["group"]?.GetValue<string>() is { } group && existing["group"] is null)
                    existing["group"] = group;
            }
            else
            {
                issues.Add($"BEHAVIOR: entityPatches targets '{ekey}.{fkey}' but that field is not a "
                    + "'select' — pick a new key for the status field");
            }
        }
    }

    /// <summary>Hang the behavior pass's calendar guard off the entity's own calendar opt-in. The
    /// domain pass decides WHETHER records reach a calendar; only the behavior pass knows the
    /// lifecycle states worth hiding, so the guard arrives separately and is lowered here. An
    /// entity that never opted in is a repair-guiding issue rather than a silent opt-in: putting
    /// records into somebody's calendar is not a decision a hide rule should make by itself.
    /// `calendar: true` widens to the object form so the guard has somewhere to live.</summary>
    private static void ApplyCalendarHideWhen(JsonObject ent, string ekey, JsonNode hideWhen,
        List<string> issues)
    {
        switch (ent["calendar"])
        {
            case JsonObject cal:
                cal["hideWhen"] = hideWhen.DeepClone();
                break;
            case JsonValue v when v.TryGetValue<bool>(out var on) && on:
                ent["calendar"] = new JsonObject { ["hideWhen"] = hideWhen.DeepClone() };
                break;
            default:
                issues.Add($"BEHAVIOR: entityPatches sets 'calendarHideWhen' on '{ekey}', but that "
                    + "entity has no calendar — drop the guard");
                break;
        }
    }

    /// <summary>Deterministic states-as-source-of-truth normalization: a select a process governs
    /// carries NO options/default/initial (the process's states are its options, initialState its
    /// default). Stripping here instead of erroring saves a whole repair round when the model
    /// authored them anyway. A process whose stateField does not exist gets a repair-guiding
    /// issue naming entityPatches (the gate would reject it too, less helpfully).</summary>
    private static void NormalizeProcessStatusFields(JsonObject merged, List<string> issues)
    {
        if (merged["processes"] is not JsonArray processes) return;
        var entities = merged["entities"] as JsonArray ?? new JsonArray();
        foreach (var p in processes.OfType<JsonObject>())
        {
            if (p["entity"]?.GetValue<string>() is not { } ekey
                || p["stateField"]?.GetValue<string>() is not { } fkey) continue;
            var ent = entities.OfType<JsonObject>().FirstOrDefault(e => e["key"]?.GetValue<string>() == ekey);
            var field = (ent?["fields"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(f => f["key"]?.GetValue<string>() == fkey);
            if (field is null)
            {
                if (ent is not null)
                    issues.Add($"BEHAVIOR: process '{p["key"]?.GetValue<string>()}' governs "
                        + $"'{ekey}.{fkey}' but that field does not exist — declare it via "
                        + "entityPatches: [{ \"entity\": \"" + ekey + "\", \"statusField\": { \"key\": \""
                        + fkey + "\", ... } }]");
                continue;   // unknown entity → the gate's error is the right one
            }
            if (field["type"]?.GetValue<string>() != "select") continue;   // gate rejects with the right error
            field["role"] = "status";
            field.Remove("options");
            field.Remove("default");
            field.Remove("initial");
        }
    }
}
