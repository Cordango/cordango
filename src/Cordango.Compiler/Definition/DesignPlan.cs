// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

public sealed record PlannedView(string Key, string Label, string Type, string Entity);

/// <summary>role: home (the landing surface, always first) | workspace | settings (always last).</summary>
public sealed record PlannedPage(string Key, string Label, string? Icon, string? Entity,
    string Role, string? Brief, List<PlannedView> Views);

public sealed record PlannedDetail(string Entity, bool Form, string? Brief);

/// <summary>A signature surface with its CARDINALITY decision: scope 'global' = ONE surface over
/// the collection, placed on a page; scope 'per_record' = one per record of ScopeEntity, composed
/// INSIDE that entity's detail. The distinction the planner exists to make — "one big matrix"
/// where the domain calls for "one matrix per employee" is the named failure this fixes.</summary>
public sealed record PlannedSurface(string Name, string Shape, string Scope, string? ScopeEntity,
    string? PlacementPage, string? PlacementDetailOf, string? RowsAxis, string? ColumnsAxis,
    string? CellEntity, string Rationale);

/// <summary>The design PLAN: the app skeleton a planning pass fixes before any screen is designed.
/// All view/page keys are decided here (the roster), so the per-screen calls that follow cannot
/// collide and can each be validated in isolation against domain + roster.</summary>
public sealed record DesignPlan(JsonNode? Theme, JsonNode? Presentation,
    List<PlannedPage> Pages, List<PlannedDetail> Details, List<PlannedSurface> Surfaces)
{
    /// <summary>The raw plan document as emitted (for genlogs — carries the judgeable rationale).</summary>
    public JsonNode? Raw { get; init; }

    /// <summary>True when a PLANNER authored this (<see cref="Parse"/>), false when it was
    /// reverse-engineered from a finished definition (<see cref="FromDefinition"/>).
    /// <para>The difference matters to anything reading <c>Pages[].Views</c> as intent. A planner's
    /// list is a decision, ordered by priority. A derived list is a reconstruction that also carries
    /// ORPHAN views attached by entity as a placement hint (see FromDefinition) — a page there may
    /// well be listed with views it was never meant to render, so treating that as "this page must
    /// show these" would rewrite perfectly good hand-authored apps.</para></summary>
    public bool Authored { get; init; }

    private static string? Str(JsonNode? n, string key) => (n as JsonObject)?[key]?.GetValue<string>();

    /// <summary>Tolerant parse of an `emit_app_plan` tool call: malformed entries are dropped rather
    /// than trusted (same posture as ParseCriticIssues). Returns null when there is no plan at all.
    /// Pages are ordered deterministically: home first, settings last, workspace order preserved.
    /// Every per_record surface's ScopeEntity gets a details entry auto-added (deterministic fix,
    /// not an error — the surface has to live somewhere).</summary>
    public static DesignPlan? Parse(JsonNode? doc)
    {
        if (doc is not JsonObject o) return null;

        var pages = new List<PlannedPage>();
        foreach (var pn in o["pages"] as JsonArray ?? new JsonArray())
        {
            if (pn is not JsonObject p || Str(p, "key") is not { Length: > 0 } key) continue;
            var views = new List<PlannedView>();
            foreach (var vn in p["views"] as JsonArray ?? new JsonArray())
            {
                if (vn is not JsonObject v || Str(v, "key") is not { Length: > 0 } vkey) continue;
                views.Add(new PlannedView(vkey, Str(v, "label") ?? vkey,
                    Str(v, "type") ?? "table", Str(v, "entity") ?? ""));
            }
            var role = Str(p, "role") switch { "home" => "home", "settings" => "settings", _ => "workspace" };
            pages.Add(new PlannedPage(key, Str(p, "label") ?? key, Str(p, "icon"),
                Str(p, "entity"), role, Str(p, "brief"), views));
        }
        pages = pages.Where(p => p.Role == "home")
            .Concat(pages.Where(p => p.Role == "workspace"))
            .Concat(pages.Where(p => p.Role == "settings")).ToList();

        var details = new List<PlannedDetail>();
        foreach (var dn in o["details"] as JsonArray ?? new JsonArray())
            if (dn is JsonObject d && Str(d, "entity") is { Length: > 0 } ent
                && details.All(x => x.Entity != ent))
                details.Add(new PlannedDetail(ent,
                    (d["form"] as JsonValue)?.TryGetValue<bool>(out var f) == true && f, Str(d, "brief")));

        var surfaces = new List<PlannedSurface>();
        foreach (var sn in o["surfaces"] as JsonArray ?? new JsonArray())
        {
            if (sn is not JsonObject s || Str(s, "name") is not { Length: > 0 } name) continue;
            var placement = s["placement"] as JsonObject;
            var axes = s["axes"] as JsonObject;
            surfaces.Add(new PlannedSurface(name, Str(s, "shape") ?? "list",
                Str(s, "scope") == "per_record" ? "per_record" : "global",
                Str(s, "scopeEntity"), Str(placement, "page"), Str(placement, "detailOf"),
                Str(axes, "rows"), Str(axes, "columns"), Str(axes, "cellEntity"),
                Str(s, "rationale") ?? ""));
        }
        // A per_record surface lives in its scope entity's detail — make sure that detail is planned.
        foreach (var s in surfaces)
            if (s.Scope == "per_record" && s.ScopeEntity is { Length: > 0 } se
                && details.All(d => d.Entity != se))
                details.Add(new PlannedDetail(se, Form: false, Brief: $"Hosts the '{s.Name}' surface"));

        return new DesignPlan(o["theme"], o["presentation"], pages, details, surfaces)
            { Raw = o, Authored = true };
    }

    /// <summary>Derives the plan from a stored MERGED definition (for refine): each page owns the
    /// views its block tree embeds; views no page embeds attach to the page whose entity matches
    /// (else the first page); entities with an authored detail/form become detail slices. The
    /// derived roster is what lets refine regenerate one slice and validate it in isolation.</summary>
    public static DesignPlan? FromDefinition(JsonNode? definition)
    {
        if (definition is not JsonObject def) return null;
        var allViews = new Dictionary<string, PlannedView>();
        var viewEntity = new Dictionary<string, string>();
        foreach (var vn in def["views"] as JsonArray ?? new JsonArray())
        {
            if (vn is not JsonObject v || Str(v, "key") is not { Length: > 0 } vkey) continue;
            var pv = new PlannedView(vkey, Str(v, "label") ?? vkey, Str(v, "type") ?? "table", Str(v, "entity") ?? "");
            allViews[vkey] = pv;
            viewEntity[vkey] = pv.Entity;
        }

        var pages = new List<PlannedPage>();
        var owned = new HashSet<string>();
        var rawPages = (def["pages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        foreach (var p in rawPages)
        {
            if (Str(p, "key") is not { Length: > 0 } key) continue;
            var views = new List<PlannedView>();
            foreach (var vkey in CollectViewRefs(p["blocks"]))
                if (owned.Add(vkey) && allViews.TryGetValue(vkey, out var pv)) views.Add(pv);
            var role = pages.Count == 0 ? "home" : Str(p, "group") == "config" ? "settings" : "workspace";
            pages.Add(new PlannedPage(key, Str(p, "label") ?? key, Str(p, "icon"), Str(p, "entity"), role, null, views));
        }
        // Orphan views: attach to the page about the same entity, else the first page.
        foreach (var (vkey, pv) in allViews)
        {
            if (owned.Contains(vkey) || pages.Count == 0) continue;
            var host = pages.FirstOrDefault(p => p.Entity == pv.Entity) ?? pages[0];
            host.Views.Add(pv);
        }

        var details = new List<PlannedDetail>();
        foreach (var en in def["entities"] as JsonArray ?? new JsonArray())
            if (en is JsonObject ent && Str(ent, "key") is { Length: > 0 } ekey
                && (ent["detail"] is JsonObject || ent["form"] is JsonObject))
                details.Add(new PlannedDetail(ekey, ent["form"] is JsonObject, null));

        return pages.Count == 0 && details.Count == 0 ? null
            : new DesignPlan(def["theme"], def["presentation"], pages, details, new List<PlannedSurface>());
    }

    /// <summary>Every view key a block tree embeds via {kind:'view'} — including inside tabs,
    /// columns and nested blocks.</summary>
    public static IEnumerable<string> CollectViewRefs(JsonNode? blocks)
    {
        var found = new List<string>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject b:
                    if (Str(b, "kind") == "view" && Str(b, "view") is { Length: > 0 } vk) found.Add(vk);
                    Walk(b["blocks"]);
                    Walk(b["columns"]);
                    foreach (var t in b["tabs"] as JsonArray ?? new JsonArray()) Walk((t as JsonObject)?["blocks"]);
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }
        Walk(blocks);
        return found.Distinct();
    }
}
