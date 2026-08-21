// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Deterministic completion of a merged design. It only ever FILLS A HOLE — it never removes,
/// rewrites or reorders what a slice authored, and it never fails.
///
/// <para>Two holes, both taken from the same live failure (2026-08-02, MeetingPrep): a page that
/// lists records people create with no reachable way to create one, and a planned primary surface
/// that no page ever placed. Both are properties of the finished document, which is why they are
/// settled here rather than asked for in a prompt: doctrine tells the model what to aim at, this
/// guarantees the result. Doctrine can be ignored; a finalizer cannot.</para>
///
/// <para><b>Fills, not errors.</b> These could have been slice-gate rules, but a
/// <c>ValidateSlice</c> failure that exhausts its one repair makes the whole slice return null and
/// <c>DesignAssembler</c> then drops that page AND its views. Failing a rich, correct page over a
/// missing button would be a strictly worse app than adding the button.</para>
/// </summary>
public static class DesignDefaults
{
    /// <summary>Complete <paramref name="merged"/> in place. Returns a note per fill applied, for
    /// the run outcome — a silent fill would hide that the model left the hole.</summary>
    public static List<string> Apply(JsonNode domainDef, DesignPlan? plan, JsonObject merged)
    {
        var notes = new List<string>();
        if (merged["pages"] is not JsonArray pages) return notes;

        var entities = (merged["entities"] as JsonArray ?? domainDef["entities"] as JsonArray)
            ?.OfType<JsonObject>().ToList() ?? [];
        var byKey = entities.Where(e => Str(e, "key") is not null)
                            .ToDictionary(e => Str(e, "key")!, e => e);
        var viewsByKey = (merged["views"] as JsonArray)?.OfType<JsonObject>()
            .Where(v => Str(v, "key") is not null)
            .ToDictionary(v => Str(v, "key")!, v => v) ?? [];

        foreach (var page in pages.OfType<JsonObject>())
            EnsurePrimaryViewIsPlaced(page, plan, viewsByKey, notes);

        // Creation is an APP-WIDE floor, not a per-page rule, and the scan deliberately ignores
        // visibility. Two reasons, both learned from the corpus:
        //   · Per-page rewrites good apps. sales-crm's "My Day" lists deals and deliberately has no
        //     New button because the Deals page owns creating them.
        //   · A tab and a segmented toggle are the same affordance — one labelled click. sales-crm
        //     puts the activity table in a third tab; calling that unreachable while calling a
        //     mode toggle reachable would be a distinction with no basis in what a user can do.
        // So this guarantees only the floor: an entity people create is creatable SOMEWHERE. That
        // floor is real (a generated app can miss it entirely) but it is not the whole of the
        // 2026-08-02 complaint — a create path parked behind a non-default toggle, next to a dead
        // display.text styled to look like a button, satisfies this and still reads as "no New
        // button". That part is doctrine's job (S5) and a critic's, not a fill's.
        var creatable = pages.OfType<JsonObject>()
            .SelectMany(p => AllBlocks(p["blocks"]).Select(b => CreatesEntity(b, viewsByKey)))
            .Where(e => e is not null).ToHashSet(StringComparer.Ordinal)!;
        foreach (var page in pages.OfType<JsonObject>())
            EnsureCreatePath(page, plan, byKey, viewsByKey, creatable, notes);
        return notes;
    }

    /// <summary>
    /// Entities a person is expected to create and cannot, anywhere in the app — the read-only form
    /// of <see cref="EnsureCreatePath"/>'s floor, over a whole document.
    ///
    /// <para>The fill and this check share <see cref="CreatesEntity"/> deliberately. Two
    /// implementations of "is there a create path" would eventually disagree, and the disagreement
    /// would surface as a smoke check failing an app the finalizer believed it had already fixed —
    /// with no way to tell which one was right.</para>
    ///
    /// <para>Why a hole survives the fill at all: it declines when the entity is shown on no page,
    /// or when nothing anywhere authored a form to open. Both leave a real app a user cannot put
    /// data into, which is a failure to report rather than a hole to paper over.</para>
    /// </summary>
    public static List<string> EntitiesWithoutCreatePath(JsonNode? doc)
    {
        if (doc is not JsonObject root || root["pages"] is not JsonArray pages) return [];
        var viewsByKey = (root["views"] as JsonArray)?.OfType<JsonObject>()
            .Where(v => Str(v, "key") is not null)
            .ToDictionary(v => Str(v, "key")!, v => v) ?? [];

        var creatable = pages.OfType<JsonObject>()
            .SelectMany(p => AllBlocks(p["blocks"]).Select(b => CreatesEntity(b, viewsByKey)))
            .Where(e => e is not null).ToHashSet(StringComparer.Ordinal)!;

        return (root["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => Str(e, "key") is not null && IsOwnSubject(e))
            .Select(e => Str(e, "key")!)
            .Where(k => !creatable.Contains(k))
            .ToList();
    }

    /// <summary>An entity a person browses and creates in its own right: a collection (not a
    /// singleton, not a config lookup) that is nobody's child — neither an <c>ownedBy</c> parent's
    /// nor the Forms archetype's.</summary>
    public static bool IsOwnSubject(JsonObject entity) =>
        (Str(entity, "kind") ?? "collection") == "collection" && !Archetypes.IsSubordinate(entity);

    /// <summary>
    /// Required fields a create form does not offer — "entity.field" for each.
    ///
    /// <para>The field-level twin of <see cref="EntitiesWithoutCreatePath"/>, and it exists because
    /// having a create path is not the same as being able to complete it. An AUTHORED form is
    /// authoritative: the renderer shows exactly what it lists and nothing else, deliberately, so a
    /// required field the form omits is a field with no control anywhere. The dialog then opens,
    /// refuses to save, and names a field that is not on screen — which is precisely what a budget app
    /// did on every entity it wrote (live 2026-08-05: <c>'name' is required, 'case_type' is
    /// required</c>, on a form showing neither).</para>
    ///
    /// <para>Read from the MANIFEST, after compilation, because the flags that decide whether a field
    /// is fillable at all — <c>system</c>, <c>readOnly</c>, <c>governedBy</c>, <c>setByCommand</c>,
    /// computed — are stamped there and not in the document the model wrote.</para>
    ///
    /// <para>Entities with no authored form are skipped: the renderer derives sections from every
    /// editable field, so nothing can be missing.</para>
    /// </summary>
    public static List<string> UnfillableRequiredFields(JsonNode? manifest)
    {
        var missing = new List<string>();
        if (manifest is not JsonObject root) return missing;

        foreach (var entity in (root["entities"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(entity, "key") is not { } key) continue;
            if (entity["form"]?["blocks"] is not JsonArray blocks || blocks.Count == 0) continue;

            var offered = new HashSet<string>(StringComparer.Ordinal);
            CollectFormFields(blocks, offered);

            // The link to the owning parent is supplied by WHERE the form was opened, not typed. An
            // owned child is created from inside its parent, and the runtime seeds that reference and
            // hides it — a form listing it would be asking somebody to re-state where they already
            // are. So it counts as offered even though the form does not mention it.
            if (Str(entity["ownedBy"] as JsonObject, "via") is { } via) offered.Add(via);

            foreach (var field in (entity["fields"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Str(field, "key") is not { } fk || offered.Contains(fk)) continue;
                if (field["required"]?.GetValue<bool>() != true) continue;
                // Not the user's to supply, or deliberately not asked for at create time.
                if (field["system"]?.GetValue<bool>() == true
                    || field["readOnly"]?.GetValue<bool>() == true
                    || field["hideOnCreate"]?.GetValue<bool>() == true
                    || field["setByCommand"]?.GetValue<bool>() == true
                    || field["governedBy"] is not null
                    || field["computed"] is JsonObject
                    || field["auto"] is not null
                    || field["default"] is not null) continue;

                missing.Add($"{key}.{fk}");
            }
        }
        return missing;
    }

    /// <summary>Every field key any <c>fields</c> block in a form tree offers, at any depth.</summary>
    private static void CollectFormFields(JsonNode? node, HashSet<string> into)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var item in arr) CollectFormFields(item, into);
                break;
            case JsonObject obj:
                if (Str(obj, "kind") == "fields")
                    foreach (var f in (obj["fields"] as JsonArray ?? []).OfType<JsonValue>())
                        if (f.TryGetValue<string>(out var k)) into.Add(k);
                foreach (var kv in obj) CollectFormFields(kv.Value, into);
                break;
        }
    }

    // ---- reachability ---------------------------------------------------------------------------

    /// <summary>A page's screen state as it is on load: <c>state[].default</c>, keyed by state key.
    /// This is the whole reason the checks below are about REACHABILITY rather than existence.</summary>
    private static Dictionary<string, string?> InitialState(JsonObject page)
    {
        var s = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var n in page["state"] as JsonArray ?? [])
            if (n is JsonObject st && Str(st, "key") is { } k)
                s[k] = st["default"] is { } d && d.GetValueKind() != System.Text.Json.JsonValueKind.Null
                    ? d.ToString() : null;
        return s;
    }

    /// <summary>Whether a block is visible when the page loads.
    /// <para>Only <c>{{state.x}}</c> is resolvable at author time. <c>{{record.*}}</c>,
    /// <c>{{actor.*}}</c> and <c>{{today}}</c> depend on data this code cannot see, so they are
    /// treated as VISIBLE: the cost of guessing "visible" is a fill that was not needed, while
    /// guessing "hidden" would add a duplicate button to a page that already had one. Under-fire on
    /// purpose.</para></summary>
    private static bool VisibleOnLoad(JsonObject block, Dictionary<string, string?> state)
    {
        if (block["visibleWhen"] is not JsonObject vw) return true;
        var expr = Str(vw, "value");
        if (expr is null) return true;
        var trimmed = expr.Trim();
        if (!(trimmed.StartsWith("{{") && trimmed.EndsWith("}}"))) return true;   // a literal — leave it
        var path = trimmed[2..^2].Trim();
        if (!path.StartsWith("state.", StringComparison.Ordinal)) return true;    // unresolvable → visible
        if (!state.TryGetValue(path["state.".Length..], out var actual)) return true;

        if (vw["eq"] is { } eq) return Same(actual, eq);
        if (vw["neq"] is { } neq) return !Same(actual, neq);
        if (vw["in"] is JsonArray arr) return arr.Any(o => o is not null && Same(actual, o));
        return true;
    }

    private static bool Same(string? actual, JsonNode expected) =>
        string.Equals(actual, expected.ToString(), StringComparison.Ordinal);

    /// <summary>Every block in a tree, whatever its visibility — "can a user get here at all".</summary>
    private static List<JsonObject> AllBlocks(JsonNode? blocks)
    {
        var found = new List<JsonObject>();
        void Walk(JsonNode? bs)
        {
            foreach (var n in bs as JsonArray ?? [])
            {
                if (n is not JsonObject b) continue;
                found.Add(b);
                Walk(b["blocks"]);
                foreach (var t in b["tabs"] as JsonArray ?? []) if (t is JsonObject to) Walk(to["blocks"]);
                foreach (var col in b["columns"] as JsonArray ?? []) Walk(col);
            }
        }
        Walk(blocks);
        return found;
    }

    /// <summary>Every block reachable in the page's initial state, in document order.</summary>
    private static List<JsonObject> ReachableBlocks(JsonObject page)
    {
        var state = InitialState(page);
        var found = new List<JsonObject>();
        void Walk(JsonNode? blocks)
        {
            foreach (var n in blocks as JsonArray ?? [])
            {
                if (n is not JsonObject b || !VisibleOnLoad(b, state)) continue;
                found.Add(b);
                Walk(b["blocks"]);
                // A tabs block only shows its FIRST tab on load; the rest are a click away, which is
                // exactly the "one click from broken" this is trying not to mistake for present.
                if (b["tabs"] is JsonArray tabs && tabs.FirstOrDefault() is JsonObject t0) Walk(t0["blocks"]);
                foreach (var col in b["columns"] as JsonArray ?? []) Walk(col);
            }
        }
        Walk(page["blocks"]);
        return found;
    }

    // ---- the two fills --------------------------------------------------------------------------

    /// <summary>A page whose planned views are ALL unreachable on load gets its primary one placed.
    /// <para>The plan lists a page's views in priority order, so the first is its primary surface —
    /// no new plan vocabulary is needed to know which one matters. Fires only when NONE is reachable:
    /// a page that shows one planned view and omits another has made a choice, and "planned" is not
    /// the same as "mandatory".</para>
    /// <para>Live 2026-08-02: the plan made a `calendar` view the first surface of the home page, the
    /// domain declared it correctly, and the screen designer hand-rolled a broken week grid instead
    /// and never referenced it. The only `view` block on the page pointed at `list` and sat behind
    /// <c>visibleWhen state.mode == "list"</c> while the default was <c>"week"</c> — so on load the
    /// page rendered none of its planned surfaces.</para></summary>
    private static void EnsurePrimaryViewIsPlaced(JsonObject page, DesignPlan? plan,
        Dictionary<string, JsonObject> viewsByKey, List<string> notes)
    {
        // Only a PLANNER's view list is an intent. A list reverse-engineered from a finished
        // definition also carries orphan views attached by entity, so acting on it would place
        // surfaces on hand-authored pages that never asked for them.
        if (plan is not { Authored: true }) return;
        if (Str(page, "key") is not { } pageKey) return;
        var planned = plan.Pages.FirstOrDefault(p => p.Key == pageKey)?.Views;
        if (planned is not { Count: > 0 }) return;

        var reachable = ReachableBlocks(page)
            .Where(b => Str(b, "kind") == "view")
            .Select(b => Str(b, "view"))
            .Where(v => v is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (planned.Any(v => reachable.Contains(v.Key))) return;

        var primary = planned[0];
        if (!viewsByKey.ContainsKey(primary.Key)) return;     // the view never landed — nothing to place

        (page["blocks"] as JsonArray ?? (JsonArray)(page["blocks"] = new JsonArray())).Add(
            new JsonObject
            {
                ["kind"] = "section",
                ["label"] = primary.Label,
                ["blocks"] = new JsonArray { new JsonObject { ["kind"] = "view", ["view"] = primary.Key } },
            });
        notes.Add($"page '{pageKey}': placed the planned primary view '{primary.Key}' — no planned "
                + "view was reachable when the page loads");
    }

    /// <summary>A page listing records people create gets a create path that is visible ON LOAD.
    /// <para>"Reachable", not "present", is the whole point. The 2026-08-02 page DID contain a New
    /// button — a table view whose block sat behind <c>visibleWhen state.mode == "list"</c> with the
    /// default <c>"week"</c>. Any rule phrased as "does a create path exist" passes that page; the
    /// user still could not create a meeting.</para></summary>
    private static void EnsureCreatePath(JsonObject page, DesignPlan? plan,
        Dictionary<string, JsonObject> byKey, Dictionary<string, JsonObject> viewsByKey,
        HashSet<string> creatableSomewhere, List<string> notes)
    {
        if (Str(page, "entity") is not { } entity) return;
        if (creatableSomewhere.Contains(entity)) return;      // some page already offers it
        if (!byKey.TryGetValue(entity, out var ent)) return;
        // Only a primary collection: a singleton has nothing to create, an owned child is created from
        // its parent's detail, and a Forms child (a submission, an answer) is written by the intake
        // machinery rather than by anyone clicking New.
        if (!IsOwnSubject(ent)) return;
        // Without an authored form there is no create surface to open.
        if (plan is not null && !plan.Details.Any(d => d.Entity == entity && d.Form)
            && ent["form"] is null) return;

        var reachable = ReachableBlocks(page);
        // Only fire where the entity is actually shown — a create button on a page that never
        // displays the records would be an orphan.
        if (!reachable.Any(b => Renders(b, entity, viewsByKey))) return;

        // Prefer switching on a list that can draw the button itself; it reads as part of the list
        // rather than as a stray button above it.
        var host = reachable.FirstOrDefault(b => Str(b, "kind") is "table" or "board"
                                                 && SourceEntity(b) == entity);
        if (host is not null)
        {
            host["newButton"] = true;
            notes.Add($"page '{Str(page, "key")}': enabled the New button on the {Str(host, "kind")} "
                    + $"of '{entity}' — the app had no create path for it on any page");
            return;
        }
        var blocks = page["blocks"] as JsonArray ?? (JsonArray)(page["blocks"] = new JsonArray());
        blocks.Insert(0, new JsonObject { ["kind"] = "create", ["entity"] = entity });
        notes.Add($"page '{Str(page, "key")}': added a create button for '{entity}' — the app had "
                + "no create path for it on any page");
    }

    /// <summary>The entity this block can actually open a create form for, or null.
    /// <para>Note which surfaces do NOT appear: a `calendar`, `timeline`, `gantt` or `dashboard`
    /// view block is wired to open records only — <c>BlockRenderer</c> never hands it a create
    /// handler — so a page whose only surface is a calendar genuinely cannot create anything.</para></summary>
    private static string? CreatesEntity(JsonObject b, Dictionary<string, JsonObject> viewsByKey) =>
        Str(b, "kind") switch
        {
            "create" => Str(b, "entity"),
            "form" => SourceEntity(b) ?? Str(b, "entity"),
            "table" or "board" => b["newButton"]?.GetValue<bool>() == true ? SourceEntity(b) : null,
            // A table/kanban VIEW block draws an unconditional New button (TableView/KanbanView).
            "view" => ViewOf(b, viewsByKey) is { } v && Str(v, "type") is "table" or "kanban"
                      ? Str(v, "entity") : null,
            "cell" => b["editable"]?.GetValue<bool>() == true ? Str(b, "entity") : null,
            "child" => Str(b, "entity"),
            _ => null,
        };

    /// <summary>Whether the block puts records of <paramref name="entity"/> on the page at all.</summary>
    private static bool Renders(JsonObject b, string entity, Dictionary<string, JsonObject> viewsByKey) =>
        RendersEntity(b, viewsByKey) == entity;

    /// <summary>The entity whose records this block puts on the page, or null.</summary>
    private static string? RendersEntity(JsonObject b, Dictionary<string, JsonObject> viewsByKey) =>
        Str(b, "kind") switch
        {
            "view" => ViewOf(b, viewsByKey) is { } v ? Str(v, "entity") : null,
            "table" or "board" or "repeat" or "calendar" or "timeline" or "gantt" or "orgchart"
                => SourceEntity(b),
            "cell" or "child" or "form" => Str(b, "entity"),
            _ => null,
        };

    /// <summary>Every entity whose records appear SOMEWHERE in the app — on a page's blocks or as a
    /// page's own subject. An entity outside this set is data nobody can see.
    /// <para>Shares <see cref="RendersEntity"/> with the create-path fill for the same reason
    /// <see cref="EntitiesWithoutCreatePath"/> does: one answer to "does this block show that
    /// entity", not two that can drift apart.</para></summary>
    public static HashSet<string> EntitiesShown(JsonNode? doc)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (doc is not JsonObject root || root["pages"] is not JsonArray pages) return shown;
        var viewsByKey = (root["views"] as JsonArray)?.OfType<JsonObject>()
            .Where(v => Str(v, "key") is not null)
            .ToDictionary(v => Str(v, "key")!, v => v) ?? [];

        foreach (var page in pages.OfType<JsonObject>())
        {
            if (Str(page, "entity") is { } pe) shown.Add(pe);
            foreach (var b in AllBlocks(page["blocks"]))
                if (RendersEntity(b, viewsByKey) is { } e) shown.Add(e);
        }
        return shown;
    }

    private static JsonObject? ViewOf(JsonObject b, Dictionary<string, JsonObject> viewsByKey) =>
        Str(b, "view") is { } k && viewsByKey.TryGetValue(k, out var v) ? v : null;

    private static string? SourceEntity(JsonObject b) => Str(b["source"] as JsonObject, "entity");

    private static string? Str(JsonNode? n, string key) =>
        (n as JsonObject)?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
