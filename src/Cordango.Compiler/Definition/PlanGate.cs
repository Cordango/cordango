// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>One per-screen generation unit. Kind 'page' = a page plus the views it owns;
/// 'detail' = an entity's record detail + form; 'theme' = theme/presentation only (refine).</summary>
public sealed record SliceSpec(string Id, string Kind, PlannedPage? Page, PlannedDetail? Detail,
    List<PlannedSurface> Surfaces, string Label);

/// <summary>
/// Deterministic validation for the design PLAN and for each per-screen SLICE, built ON the real
/// Gate via skeleton substitution: the domain plus a stub table view for every planned view key
/// and a stub text-block page for every planned page is a valid document BY CONSTRUCTION, so when
/// one slice's real output is swapped in, every gate error is attributable to that slice — while
/// cross-slice references (a page embedding its planned views) still resolve against the roster.
/// </summary>
public static class PlanGate
{
    /// <summary>The slices a plan generates, in generation order: pages in plan order (home
    /// first), then details. Each slice carries the planned surfaces placed on it.</summary>
    public static List<SliceSpec> Slices(DesignPlan plan)
    {
        var slices = new List<SliceSpec>();
        foreach (var p in plan.Pages)
            slices.Add(new SliceSpec($"page:{p.Key}", "page", p, null,
                plan.Surfaces.Where(s => s.Scope == "global" && s.PlacementPage == p.Key).ToList(),
                p.Label));
        foreach (var d in plan.Details)
            slices.Add(new SliceSpec($"detail:{d.Entity}", "detail", null, d,
                plan.Surfaces.Where(s => s.Scope == "per_record"
                    && (s.PlacementDetailOf ?? s.ScopeEntity) == d.Entity).ToList(),
                $"{Humanize(d.Entity)} details"));
        return slices;
    }

    /// <summary>The theme pseudo-slice id (refine targeting only — generation gets theme from the plan).</summary>
    public const string ThemeSliceId = "theme";

    private static string Humanize(string key) =>
        string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>Domain + plan roster as a full, gate-valid document. Stub views are always type
    /// 'table' (no data-shape preconditions, so a stub can never fail); stub pages carry a single
    /// resolution-free text block. When <paramref name="substituteSliceId"/> is given, that
    /// slice's stubs are dropped and its real output from <paramref name="sliceDoc"/> inserted.</summary>
    public static JsonObject BuildSkeleton(JsonNode domainDef, DesignPlan plan,
        string? substituteSliceId = null, JsonNode? sliceDoc = null)
    {
        var doc = domainDef.DeepClone().AsObject();
        foreach (var k in new[] { "views", "pages" }) doc.Remove(k);
        if (plan.Theme is { } theme) doc["theme"] = theme.DeepClone();
        if (plan.Presentation is { } pres) doc["presentation"] = pres.DeepClone();

        var slice = sliceDoc as JsonObject;
        var views = new JsonArray();
        var pages = new JsonArray();
        foreach (var p in plan.Pages)
        {
            var substituted = substituteSliceId == $"page:{p.Key}";
            if (substituted && slice is not null)
            {
                if (slice["page"] is JsonObject realPage) pages.Add(realPage.DeepClone());
                foreach (var vn in slice["views"] as JsonArray ?? new JsonArray())
                    if (vn is not null) views.Add(vn.DeepClone());
                continue;
            }
            pages.Add(new JsonObject
            {
                ["key"] = p.Key,
                ["label"] = p.Label,
                ["blocks"] = new JsonArray(new JsonObject { ["kind"] = "text", ["value"] = "…" }),
            });
            foreach (var v in p.Views)
                views.Add(new JsonObject
                {
                    ["key"] = v.Key, ["label"] = v.Label, ["type"] = "table", ["entity"] = v.Entity,
                });
        }
        doc["views"] = views;
        doc["pages"] = pages;

        if (substituteSliceId?.StartsWith("detail:") == true && slice is not null)
        {
            var ekey = substituteSliceId["detail:".Length..];
            var ent = (doc["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .FirstOrDefault(e => e["key"]?.GetValue<string>() == ekey);
            if (ent is not null)
            {
                if ((slice["detail"] as JsonObject)?["blocks"] is JsonArray db && db.Count > 0)
                    ent["detail"] = new JsonObject { ["blocks"] = db.DeepClone() };
                if ((slice["form"] as JsonObject)?["blocks"] is JsonArray fb && fb.Count > 0)
                    ent["form"] = new JsonObject { ["blocks"] = fb.DeepClone() };
            }
        }
        return doc;
    }

    /// <summary>Validates the PLAN: plan-only invariants (prefixed 'PLAN:') plus the full Gate on
    /// the stub skeleton — which catches unknown entities, views over subordinate/settings
    /// entities, bad theme structure, and duplicate keys, all attributable to the plan itself.</summary>
    public static List<string> ValidatePlan(JsonNode domainDef, DesignPlan plan)
    {
        var errors = new List<string>();
        if (plan.Pages.Count == 0)
        {
            errors.Add("PLAN: the plan authored no pages. Call `emit_app_plan` with the complete "
                + "skeleton — every page (home first), each page's views, and the detail roster.");
            return errors;
        }
        if (plan.Pages.Count(p => p.Role == "home") == 0)
            errors.Add("PLAN: no page has role 'home' — exactly one page must be the landing surface.");
        if (plan.Pages.Count(p => p.Role == "home") > 1)
            errors.Add("PLAN: more than one page has role 'home' — exactly one page is the landing surface.");

        var pageKeys = new HashSet<string>();
        foreach (var p in plan.Pages)
            if (!pageKeys.Add(p.Key)) errors.Add($"PLAN: duplicate page key '{p.Key}'");
        var viewKeys = new HashSet<string>();
        foreach (var v in plan.Pages.SelectMany(p => p.Views))
            if (!viewKeys.Add(v.Key)) errors.Add($"PLAN: duplicate view key '{v.Key}' — view keys must be unique across ALL pages");

        var entityKeys = (domainDef as JsonObject)?["entities"] is JsonArray ents
            ? ents.OfType<JsonObject>().Select(e => e["key"]?.GetValue<string>())
                .Where(k => k is not null).Cast<string>().ToHashSet()
            : new HashSet<string>();
        foreach (var s in plan.Surfaces)
        {
            var where = $"PLAN: surface '{s.Name}'";
            if (s.Scope == "per_record")
            {
                if (string.IsNullOrEmpty(s.ScopeEntity))
                    errors.Add($"{where} is per_record but names no scopeEntity — whose record hosts it?");
                else if (!entityKeys.Contains(s.ScopeEntity))
                    errors.Add($"{where} scopeEntity '{s.ScopeEntity}' is not a domain entity");
                if (s.PlacementDetailOf is { } det && det != s.ScopeEntity)
                    errors.Add($"{where} is per_record of '{s.ScopeEntity}' but placed in the detail of "
                        + $"'{det}' — a per-record surface lives in ITS OWN entity's detail");
            }
            else
            {
                if (string.IsNullOrEmpty(s.PlacementPage))
                    errors.Add($"{where} is global but placement.page names no page");
                else if (!pageKeys.Contains(s.PlacementPage))
                    errors.Add($"{where} placement.page '{s.PlacementPage}' is not a planned page");
            }
        }
        foreach (var d in plan.Details)
            if (!entityKeys.Contains(d.Entity))
                errors.Add($"PLAN: details entry references unknown entity '{d.Entity}'");

        if (errors.Count > 0) return errors;
        return Gate.Validate(BuildSkeleton(domainDef, plan));
    }

    /// <summary>Validates ONE slice's output: degenerate/ownership checks (a page slice must
    /// contain its planned page and all its planned views, and may not author or embed another
    /// slice's views), then the full Gate on the skeleton with this slice's real output swapped
    /// in — so every reported error belongs to this slice.</summary>
    public static List<string> ValidateSlice(JsonNode domainDef, DesignPlan plan, SliceSpec spec,
        JsonNode? sliceDoc)
    {
        var errors = new List<string>();
        if (sliceDoc is not JsonObject slice)
            return new List<string> { Degenerate(spec) };

        switch (spec.Kind)
        {
            case "page":
            {
                var page = slice["page"] as JsonObject;
                if (page is null) return new List<string> { Degenerate(spec) };
                var planned = spec.Page!;
                if (page["key"]?.GetValue<string>() is var pk && pk != planned.Key)
                    errors.Add($"SLICE: the page key must be '{planned.Key}' (the plan fixed it), not '{pk}'");

                var ownKeys = planned.Views.Select(v => v.Key).ToHashSet();
                var emitted = new HashSet<string>();
                foreach (var vn in slice["views"] as JsonArray ?? new JsonArray())
                {
                    if (vn is not JsonObject v || v["key"]?.GetValue<string>() is not { } vk) continue;
                    emitted.Add(vk);
                    if (!ownKeys.Contains(vk))
                        errors.Add($"SLICE: view '{vk}' is not assigned to this screen — emit EXACTLY "
                            + $"the planned views ({string.Join(", ", ownKeys)}) and nothing else");
                }
                foreach (var missing in ownKeys.Except(emitted))
                    errors.Add($"SLICE: the planned view '{missing}' was not emitted — this screen owns it");
                foreach (var refKey in DesignPlan.CollectViewRefs(page["blocks"]))
                    if (!ownKeys.Contains(refKey))
                        errors.Add($"SLICE: the page embeds view '{refKey}', which belongs to another "
                            + "screen — a page may only embed its own views");
                break;
            }
            case "detail":
            {
                var planned = spec.Detail!;
                var detail = slice["detail"] as JsonObject;
                if (detail is null || detail["blocks"] is not JsonArray { Count: > 0 })
                    return new List<string> { Degenerate(spec) };
                if (detail["entity"]?.GetValue<string>() is { } de && de != planned.Entity)
                    errors.Add($"SLICE: the detail must target entity '{planned.Entity}', not '{de}'");
                if (planned.Form && (slice["form"] as JsonObject)?["blocks"] is not JsonArray { Count: > 0 })
                    errors.Add($"SLICE: the plan calls for a form for '{planned.Entity}' — emit `form` "
                        + "with its section/fields blocks");
                if ((slice["form"] as JsonObject)?["entity"]?.GetValue<string>() is { } fe && fe != planned.Entity)
                    errors.Add($"SLICE: the form must target entity '{planned.Entity}', not '{fe}'");
                break;
            }
            case "theme":
                if (slice["theme"] is null && slice["presentation"] is null)
                    return new List<string> { Degenerate(spec) };
                break;
        }
        if (errors.Count > 0) return errors;
        return Gate.Validate(BuildSkeleton(domainDef, plan, spec.Id, slice));
    }

    private static string Degenerate(SliceSpec spec) => spec.Kind switch
    {
        "page" => $"SLICE: the screen authored nothing. Call `emit_screen` with `page` (key "
            + $"'{spec.Page!.Key}') and its `views` — not a placeholder.",
        "detail" => $"SLICE: the screen authored nothing. Call `emit_screen` with `detail` for "
            + $"entity '{spec.Detail!.Entity}' (and `form` when planned) — not a placeholder.",
        _ => "SLICE: the screen authored nothing. Call `emit_screen` with `theme`/`presentation`.",
    };
}
