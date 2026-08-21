// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Applies a design-pass document onto a validated domain definition. The design pass may ONLY
/// author presentation-layer keys — theme, presentation, views, pages, and per-entity
/// detail/form layouts. Domain sections (entities' fields, relations, workflows, roles, plugins,
/// identity keys) are never copied, so the design model is structurally incapable of corrupting
/// the domain no matter what it emits.
/// </summary>
public static class DesignMerge
{
    /// <summary>Top-level design keys copied wholesale onto the definition.</summary>
    private static readonly string[] TopLevelKeys = ["theme", "presentation", "views", "pages"];

    /// <summary>The ONE way a design document becomes final: merge, then deterministic completion,
    /// then the whole-document gate. Every path that produces a final design — initial composition,
    /// critic repair, refinement — calls this, so the invariants <see cref="DesignDefaults"/>
    /// guarantees are properties of the PLATFORM and not of one code path.
    /// <para>Order matters. Completion runs AFTER the merge, on the finished document, so it can
    /// never be undone by a later copy and so it sees the same entities and views the gate will.
    /// It then runs BEFORE the gate, so a fill is validated exactly like authored content — a fill
    /// that produced an invalid document would fail loudly here rather than ship.</para></summary>
    public static (JsonNode Merged, List<string> Issues, List<string> Errors) Finalize(
        JsonNode domainDef, DesignPlan? plan, JsonNode? designDoc)
    {
        var merged = Apply(domainDef, designDoc, out var issues);
        if (merged is JsonObject obj) issues.AddRange(DesignDefaults.Apply(domainDef, plan, obj));
        var errors = issues.Where(i => i.StartsWith("DESIGN:", StringComparison.Ordinal))
                           .Concat(Gate.Validate(merged)).ToList();
        return (merged, issues, errors);
    }

    /// <summary>Merge <paramref name="designDoc"/> into a deep clone of <paramref name="domainDef"/>.
    /// <paramref name="issues"/> collects references the merge had to drop (e.g. a detail layout for
    /// an unknown entity) so the design repair loop can surface them to the model.</summary>
    public static JsonNode Apply(JsonNode domainDef, JsonNode? designDoc, out List<string> issues)
    {
        issues = new List<string>();
        var merged = domainDef.DeepClone().AsObject();
        if (designDoc is not JsonObject design) { issues.Add("DESIGN: the design document is not an object"); return merged; }

        foreach (var key in TopLevelKeys)
            if (design[key] is { } v)
                merged[key] = v.DeepClone();

        ApplyEntityLayouts(merged, design["details"], "detail", issues);
        ApplyEntityLayouts(merged, design["forms"], "form", issues);
        return merged;
    }

    private static void ApplyEntityLayouts(JsonObject merged, JsonNode? list, string prop, List<string> issues)
    {
        if (list is null) return;
        if (list is not JsonArray arr)
        {
            issues.Add($"DESIGN: '{prop}s' must be an array of {{ entity, blocks }} objects");
            return;
        }
        var entities = merged["entities"] as JsonArray ?? new JsonArray();
        foreach (var item in arr)
        {
            if (item is not JsonObject o || o["entity"]?.GetValue<string>() is not { } ekey)
            {
                issues.Add($"DESIGN: every '{prop}s' entry needs an 'entity' key");
                continue;
            }
            var ent = entities.OfType<JsonObject>().FirstOrDefault(e => e["key"]?.GetValue<string>() == ekey);
            if (ent is null)
            {
                issues.Add($"DESIGN: '{prop}s' entry references unknown entity '{ekey}'");
                continue;
            }
            if (o["blocks"] is not JsonArray blocks || blocks.Count == 0)
            {
                issues.Add($"DESIGN: '{prop}s' entry for '{ekey}' has no blocks");
                continue;
            }
            ent[prop] = new JsonObject { ["blocks"] = blocks.DeepClone() };
        }
    }
}
