// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Stitches per-screen slice outputs into ONE design document (the shape DesignMerge.Apply
/// consumes), and swaps a single slice in and out of a stored definition for refine. Pure JSON —
/// no providers. The sidebar is page order — there is no navigation section to author.
/// </summary>
public static class DesignAssembler
{
    /// <summary>Assemble the design document from the plan (theme/presentation, page order) and
    /// the slices that landed. Views/pages appear in plan order; a skipped slice simply
    /// contributes nothing (the compiler's fallbacks cover the hole). Anything a slice emitted
    /// beyond its assignment is dropped and recorded in <paramref name="issues"/>.</summary>
    public static JsonObject Assemble(DesignPlan plan, IReadOnlyDictionary<string, JsonNode> landedSlices,
        out List<string> issues)
    {
        issues = new List<string>();
        var doc = new JsonObject();
        if (plan.Theme is { } theme) doc["theme"] = theme.DeepClone();
        if (plan.Presentation is { } pres) doc["presentation"] = pres.DeepClone();

        var views = new JsonArray();
        var pages = new JsonArray();
        foreach (var p in plan.Pages)
        {
            if (!landedSlices.TryGetValue($"page:{p.Key}", out var node) || node is not JsonObject slice)
                continue;
            if (slice["page"] is JsonObject page) pages.Add(page.DeepClone());
            var ownKeys = p.Views.Select(v => v.Key).ToHashSet();
            foreach (var vn in slice["views"] as JsonArray ?? new JsonArray())
            {
                if (vn is not JsonObject v) continue;
                var vkey = v["key"]?.GetValue<string>();
                if (vkey is not null && ownKeys.Contains(vkey)) views.Add(v.DeepClone());
                else issues.Add($"ASSEMBLE: dropped view '{vkey}' from screen '{p.Key}' — not in its plan assignment");
            }
        }
        doc["views"] = views;
        doc["pages"] = pages;

        var details = new JsonArray();
        var forms = new JsonArray();
        foreach (var d in plan.Details)
        {
            if (!landedSlices.TryGetValue($"detail:{d.Entity}", out var node) || node is not JsonObject slice)
                continue;
            if ((slice["detail"] as JsonObject)?["blocks"] is JsonArray db && db.Count > 0)
                details.Add(new JsonObject { ["entity"] = d.Entity, ["blocks"] = db.DeepClone() });
            if ((slice["form"] as JsonObject)?["blocks"] is JsonArray fb && fb.Count > 0)
                forms.Add(new JsonObject { ["entity"] = d.Entity, ["blocks"] = fb.DeepClone() });
        }
        doc["details"] = details;
        doc["forms"] = forms;
        return doc;
    }

    /// <summary>Remove every design-authored section from a definition, leaving the pure domain —
    /// used before a full redesign so no stale layout survives the merge.</summary>
    public static JsonObject StripDesign(JsonNode definition)
    {
        var doc = definition.DeepClone().AsObject();
        foreach (var k in new[] { "theme", "presentation", "views", "pages" })
            doc.Remove(k);
        foreach (var en in doc["entities"] as JsonArray ?? new JsonArray())
            if (en is JsonObject ent)
            {
                ent.Remove("detail");
                ent.Remove("form");
            }
        return doc;
    }

    /// <summary>Swap ONE slice's output into a stored definition IN PLACE (pass a deep clone):
    /// a page slice replaces its page and its owned views wholesale; a detail slice replaces the
    /// entity's detail/form; the theme pseudo-slice replaces theme/presentation. Everything else
    /// stays byte-identical — the whole point of per-screen refine.</summary>
    public static void ReplaceSlice(JsonObject definition, DesignPlan plan, string sliceId, JsonNode sliceDoc)
    {
        if (sliceDoc is not JsonObject slice) return;

        if (sliceId == PlanGate.ThemeSliceId)
        {
            if (slice["theme"] is { } t) definition["theme"] = t.DeepClone();
            if (slice["presentation"] is { } pr) definition["presentation"] = pr.DeepClone();
            return;
        }

        if (sliceId.StartsWith("page:"))
        {
            var pageKey = sliceId["page:".Length..];
            var planned = plan.Pages.FirstOrDefault(p => p.Key == pageKey);
            if (planned is null) return;
            var ownKeys = planned.Views.Select(v => v.Key).ToHashSet();

            if (slice["page"] is JsonObject newPage)
            {
                var pages = definition["pages"] as JsonArray ?? new JsonArray();
                var idx = pages.ToList().FindIndex(p =>
                    (p as JsonObject)?["key"]?.GetValue<string>() == pageKey);
                if (idx >= 0) pages[idx] = newPage.DeepClone();
                else pages.Add(newPage.DeepClone());
                definition["pages"] = pages;
            }

            var kept = new JsonArray();
            foreach (var vn in definition["views"] as JsonArray ?? new JsonArray())
                if (vn is JsonObject v && v["key"]?.GetValue<string>() is { } vk && !ownKeys.Contains(vk))
                    kept.Add(v.DeepClone());
            foreach (var vn in slice["views"] as JsonArray ?? new JsonArray())
                if (vn is JsonObject v && v["key"]?.GetValue<string>() is { } vk && ownKeys.Contains(vk))
                    kept.Add(v.DeepClone());
            definition["views"] = kept;
            return;
        }

        if (sliceId.StartsWith("detail:"))
        {
            var ekey = sliceId["detail:".Length..];
            var ent = (definition["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(e => e["key"]?.GetValue<string>() == ekey);
            if (ent is null) return;
            if ((slice["detail"] as JsonObject)?["blocks"] is JsonArray db && db.Count > 0)
                ent["detail"] = new JsonObject { ["blocks"] = db.DeepClone() };
            if ((slice["form"] as JsonObject)?["blocks"] is JsonArray fb && fb.Count > 0)
                ent["form"] = new JsonObject { ["blocks"] = fb.DeepClone() };
        }
    }
}
