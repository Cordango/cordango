// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compile;

/// <summary>
/// Compiles a validated <b>App Definition</b> (the design/plan the AI emits) into a runtime
/// <b>app manifest</b> — the concrete artifact the data plane and renderer consume.
///
/// This is the first, deliberately-small slice of the runtime: it does NOT touch Postgres yet.
/// It just makes explicit everything the runtime contributes on top of the definition:
///  • the base/system fields every entity gets (id, company_id, app_id, audit stamps, status,
///    soft-delete) — declared here once, so the runtime and the UI agree on them;
///  • per-entity defaults the definition may omit (labelPlural, displayField);
///  • table-view columns defaulted to the entity's declared fields when unspecified;
///  • a <c>build</c> header stamping which definition (key/version) was built, and when.
///
/// The output is its own shape (a manifest), not an App Definition, so it is intentionally NOT
/// re-validated against the App Definition schema.
/// </summary>
public static class AppCompiler
{
    public const string ManifestVersion = "1";
    public const string CompilerId = "appbuilder-runtime/0.1";

    /// <summary>
    /// Base fields the runtime materializes on EVERY entity table (mirrors
    /// <see cref="Gate.BaseFields"/>). Order is stable: id first, then business fields, then the
    /// audit/lifecycle columns. Each is flagged <c>system:true</c> + <c>readOnly:true</c> so the
    /// renderer can hide or specially-treat them.
    /// </summary>
    private static readonly (string Key, string Label, string Type)[] LeadingSystemFields =
    {
        ("id", "ID", "uuid"),
    };

    private static readonly (string Key, string Label, string Type)[] TrailingSystemFields =
    {
        ("company_id", "Company", "uuid"),
        ("app_id", "App", "uuid"),
        ("created_at", "Created", "datetime"),
        ("created_by", "Created By", "uuid"),
        ("updated_at", "Updated", "datetime"),
        ("updated_by", "Updated By", "uuid"),
        ("deleted_at", "Deleted", "datetime"),
        // Lifecycle base field. Named 'record_state' (NOT 'status') so a business field is free to
        // use the key 'status'; the process/business status a record moves through is a declared
        // role:'status' field, distinct from this active/archived record lifecycle.
        ("record_state", "Record state", "select"),
    };

    /// <summary>
    /// Compile <paramref name="definition"/> into a runtime manifest.
    /// </summary>
    /// <param name="definition">A validated App Definition.</param>
    /// <param name="appId">The runtime app id (the owning AppRecord id).</param>
    /// <param name="builtAt">Build timestamp (caller supplies it — keeps this pure/testable).</param>
    public static JsonObject Compile(JsonNode definition, string appId, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // Deep-clone so we never mutate the caller's stored definition.
        var manifest = (JsonObject)definition.DeepClone();

        // The build header's schema version decides whether the runtime will serve this app at all,
        // so it is stated by the platform here rather than copied from whatever the document claimed.
        // This is the LAST line of defence and the only one every build shares: a definition with a
        // mislabelled version was compiling into a manifest the runtime then refused, and the fix —
        // rebuilding — produced the identical manifest, which is a loop with no exit from the UI.
        AppSchemaVersion.Stamp(manifest);

        var source = new JsonObject
        {
            ["key"] = Str(manifest["key"]),
            ["name"] = Str(manifest["name"]),
            ["version"] = Str(manifest["version"]),
            ["schemaVersion"] = Str(manifest["schemaVersion"]),
        };

        // Inject the runtime-provided fields into every entity.
        foreach (var entity in Arr(manifest["entities"]).OfType<JsonObject>())
            CompileEntity(entity);

        // A computed field (expr/rollup) is the server's to write: read-only in every form and
        // cell, dropped from client input, recomputed by the data service on every write.
        FlagComputedFields(manifest);

        // Processes + commands (behavior). Canonicalize each process-governed status field's options
        // from its states, synthesize a command for every command-less transition, then resolve where
        // command buttons appear. Runs after fields materialize, before nav/pages/detail synthesis.
        CanonicalizeProcesses(manifest);
        SynthesizeCommands(manifest);
        // A field a command COLLECTS (a decline reason, an assignee picked at Assign time) is owned by
        // that command, not by whoever creates the record — it must never be freely editable in the
        // generic create/edit form. Nothing else links commands[].input.fields back to the field, so
        // stamp it here (mirrors CanonicalizeProcesses stamping `governedBy`), after both authored and
        // synthesized commands exist.
        FlagCommandOwnedFields(manifest);

        // Parent/child IA: fill in ownedBy the AI didn't tag, so subordinate entities get no page of
        // their own (the sidebar is page order) and embed in their parent's detail (see
        // SynthesizeDetails). Must run before pages are synthesized.
        InferSubordination(manifest);
        // Lookup/reference lists (territories, product types, risk tiers) are app CONFIG, not subjects —
        // tag them so the shell groups them under one "Configuration" area instead of top-level nav.
        InferConfigEntities(manifest);
        // Resolve each opted-in entity's `calendar` flag into the fields it actually means. Runs here
        // because it reads `ownedBy` (InferSubordination, just above) for the parent hop and the
        // canonicalized status options for colour, and because everything downstream should see a
        // resolved fact rather than re-deriving one.
        ResolveCalendar(manifest);
        // A domain-only definition (design pass skipped or failed) has NO views — synthesize a
        // table per primary entity so the app still builds and renders.
        DefaultViews(manifest);
        DefaultTableColumns(manifest);
        // Views exist and CanonicalizeProcesses has stamped `governedBy`: decide which boards may
        // actually be dragged before anything reads the interaction mode.
        ResolveBoardInteraction(manifest);
        // Same derive-don't-trust rule for editable cells (needs `governedBy`, stamped just above).
        ResolveCellEditing(manifest);
        // Now views exist: default command placements + surface transition commands on interactive boards.
        ResolvePlacements(manifest);
        // Respect an AI-authored layout; otherwise synthesize a default page-per-entity layout.
        if (manifest["pages"] is not JsonArray { Count: > 0 }) SynthesizePages(manifest);
        // Whichever produced the pages, collapse config/settings pages into the shell's Configuration group.
        TagConfigPages(manifest);
        // Compose a record-detail block tree per primary entity (its fields + typed child-blocks).
        SynthesizeDetails(manifest);
        // Icons + launcher presentation: validate AI choices against the vocabulary and stamp
        // keyword fallbacks, so every app/page/entity ALWAYS has a real icon (shell contract).
        ResolveIcons(manifest);

        // Stamp the build header last so it sits at the top of the document.
        var built = new JsonObject
        {
            ["kind"] = "app-manifest",
            ["manifestVersion"] = ManifestVersion,
            ["compiler"] = CompilerId,
            ["appId"] = appId,
            ["builtAt"] = builtAt.ToUniversalTime().ToString("o"),
            ["source"] = source,
        };
        return Prepend(manifest, "build", built);
    }

    private static void CompileEntity(JsonObject entity)
    {
        var declared = Arr(entity["fields"]);
        var declaredKeys = declared.OfType<JsonObject>()
            .Select(f => f["key"]?.GetValue<string>())
            .Where(k => k is not null).ToHashSet();

        // Rebuild fields = leading system + declared + trailing system, skipping any base field a
        // definition wrongly declared (the gate forbids it, but be defensive).
        var rebuilt = new JsonArray();
        foreach (var spec in LeadingSystemFields) rebuilt.Add(SystemField(spec));
        foreach (var f in declared.OfType<JsonObject>())
            if (!Gate.BaseFields.Contains(f["key"]?.GetValue<string>() ?? "")) rebuilt.Add(f.DeepClone());
        foreach (var spec in TrailingSystemFields) rebuilt.Add(SystemField(spec));
        entity["fields"] = rebuilt;

        // Post-process every field: give status/select options a sensible color, and flag
        // audit/ownership fields (e.g. submitted_by, created_by, owner) as system-set so the
        // renderer hides them from forms and never lets a user fill them.
        foreach (var f in rebuilt.OfType<JsonObject>())
        {
            AssignOptionColors(f);
            FlagAutoField(f);
        }

        // labelPlural: default to label + "s" when omitted.
        if (entity["labelPlural"] is null && entity["label"]?.GetValue<string>() is { } label)
            entity["labelPlural"] = label + "s";

        // displayField: default to the first declared text-ish field, else the first declared field.
        if (entity["displayField"] is null)
        {
            var candidate = FirstDeclaredDisplayField(declared);
            if (candidate is not null) entity["displayField"] = candidate;
        }
    }

    private static string? FirstDeclaredDisplayField(JsonArray declaredFields)
    {
        string? first = null;
        foreach (var f in declaredFields.OfType<JsonObject>())
        {
            var key = f["key"]?.GetValue<string>();
            if (key is null || Gate.BaseFields.Contains(key)) continue;
            first ??= key;
            if (f["type"]?.GetValue<string>() is "text" or "email") return key; // best label-like field
        }
        return first;
    }

    private static JsonObject SystemField((string Key, string Label, string Type) spec)
    {
        var field = new JsonObject
        {
            ["key"] = spec.Key,
            ["label"] = spec.Label,
            ["type"] = spec.Type,
            ["system"] = true,
            ["readOnly"] = true,
        };
        if (spec.Key == "record_state")
        {
            field["default"] = "active";
            field["options"] = new JsonArray(
                new JsonObject { ["value"] = "active", ["label"] = "Active" },
                new JsonObject { ["value"] = "archived", ["label"] = "Archived" });
        }
        return field;
    }

    // Fields the CURRENT USER fills implicitly (submitter / owner / audit) — hidden from all forms and
    // system-set. Anything ending in "_by" (submitted_by, created_by, entered_by …) plus these self/owner
    // names. NOTE: approval "_by" keys are handled by HideOnCreate below (an approver is NOT the creator).
    private static readonly HashSet<string> CurrentUserFieldKeys = new()
        { "owner", "author", "requester", "submitter", "reporter", "requested_by", "raised_by", "reported_by" };
    // Submission timestamps the runtime stamps on create — hidden from forms.
    private static readonly HashSet<string> CurrentTimeFieldKeys = new()
        { "submitted_at", "requested_at", "reported_at", "raised_at", "logged_at", "opened_at" };
    // Fields set LATER in the process, not by whoever creates the record: a status starts at its default;
    // an approver/decision/notes are filled during approval. Hidden from the CREATE form only (still
    // editable afterwards) — via `hideOnCreate`, so they are NOT flagged readOnly.
    private static readonly HashSet<string> HideOnCreateKeys = new()
        { "approver", "reviewer", "approved_by", "reviewed_by", "approved_at", "reviewed_at",
          "approver_notes", "approval_notes", "review_notes", "decision", "rejection_reason", "resolution_notes" };

    private static void FlagAutoField(JsonObject f)
    {
        if (f["system"]?.GetValue<bool>() == true) return;      // base fields already handled
        if (f["key"]?.GetValue<string>() is not { } key) return;

        // A process/status field or an approval-side field is noise on the CREATE form (the record
        // starts at its default / is decided later). Hide there, but keep it editable afterwards.
        var hideOnCreate = GetStr(f, "role") == "status" || HideOnCreateKeys.Contains(key);
        if (hideOnCreate) f["hideOnCreate"] = true;

        var canHoldCurrentUser = GetStr(f, "type") is "reference" or "text";
        if (!hideOnCreate && canHoldCurrentUser
            && (key.EndsWith("_by", StringComparison.Ordinal) || CurrentUserFieldKeys.Contains(key)))
        {
            f["readOnly"] = true;
            f["auto"] = "currentUser";
        }
        else if (!hideOnCreate && CurrentTimeFieldKeys.Contains(key))
        {
            f["readOnly"] = true;
            f["auto"] = "currentTime";
        }

        // A record's PUBLIC ADDRESS is generated, never typed. The author declares the role — "this
        // is where the record is served" — and the platform supplies the entropy, because the address
        // is the credential and an author who picks a memorable one has published a guessable one
        // with nothing about the working page to tell them so. Same mechanism as the two above: the
        // field becomes read-only, which is what makes `ValidateAndCopy` drop it from client input,
        // and `auto` names who fills it instead.
        if (GetStr(f, "role") == "publicToken")
        {
            f["readOnly"] = true;
            f["auto"] = "publicToken";
        }
    }

    // Deterministic, semantic-ish colors for select/status options so statuses render as colored
    // chips (green=good, red=bad, amber=in-progress, gray=neutral), falling back to a rotating palette.
    private static readonly string[] Palette =
        { "#3b82f6", "#8b5cf6", "#06b6d4", "#ec4899", "#14b8a6", "#f97316", "#64748b", "#22c55e" };

    private static void AssignOptionColors(JsonObject f)
    {
        if (f["type"]?.GetValue<string>() is not ("select" or "multiselect")) return;
        if (f["options"] is not JsonArray opts) return;
        var i = 0;
        foreach (var o in opts.OfType<JsonObject>())
        {
            if (o["color"] is null)
            {
                var val = o["value"]?.GetValue<string>() ?? o["label"]?.GetValue<string>() ?? "";
                o["color"] = SemanticColor(val) ?? Palette[i % Palette.Length];
            }
            i++;
        }
    }

    private static string? SemanticColor(string raw)
    {
        var v = raw.ToLowerInvariant();
        bool H(params string[] ks) => ks.Any(v.Contains);
        // gray first so "inactive" isn't caught by the "active" (green) rule.
        if (H("draft", "backlog", "inactive", "archiv", "closed", "cancel", "low", "new", "todo")) return "#64748b";
        if (H("reject", "denied", "fail", "lost", "error", "overdue", "block", "void", "expired", "urgent", "critical", "high")) return "#ef4444";
        if (H("pending", "review", "progress", "waiting", "hold", "escalat", "request", "open", "submit", "medium", "normal")) return "#f59e0b";
        if (H("approv", "active", "done", "complete", "won", "success", "resolve", "paid", "accept", "pass", "enabled", "yes")) return "#22c55e";
        return null;
    }

    /// <summary>For table views without explicit columns, default to the entity's declared fields.</summary>
    /// <summary>A definition with NO views (design pass skipped/failed) still needs list surfaces:
    /// synthesize one table view per primary entity. Subordinate entities render inside their
    /// parent's detail and settings singletons get their settings surface — neither needs a view.</summary>
    private static void DefaultViews(JsonObject manifest)
    {
        if (manifest["views"] is JsonArray { Count: > 0 }) return;
        var subordinate = SubordinateKeys(manifest);
        var views = new JsonArray();
        foreach (var e in Arr(manifest["entities"]).OfType<JsonObject>())
        {
            if (e["key"]?.GetValue<string>() is not { } ek) continue;
            if (subordinate.Contains(ek)) continue;
            if (e["kind"]?.GetValue<string>() == "settings") continue;
            views.Add(new JsonObject
            {
                ["key"] = ek + "_table",
                ["label"] = e["labelPlural"]?.GetValue<string>() ?? e["label"]?.GetValue<string>() ?? ek,
                ["type"] = "table",
                ["entity"] = ek,
            });
        }
        if (views.Count > 0) manifest["views"] = views;
    }

    private static void DefaultTableColumns(JsonObject manifest)
    {
        // Map entity key -> its declared (non-system) field keys, in order.
        var declaredByEntity = new Dictionary<string, List<string>>();
        foreach (var e in Arr(manifest["entities"]).OfType<JsonObject>())
        {
            if (e["key"]?.GetValue<string>() is not { } ek) continue;
            var cols = new List<string>();
            foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
            {
                var fk = f["key"]?.GetValue<string>();
                if (fk is not null && f["system"]?.GetValue<bool>() != true) cols.Add(fk);
            }
            declaredByEntity[ek] = cols;
        }

        foreach (var v in Arr(manifest["views"]).OfType<JsonObject>())
        {
            if (v["type"]?.GetValue<string>() != "table") continue;
            var config = v["config"] as JsonObject;
            if (config?["columns"] is JsonArray) continue; // already specified
            if (v["entity"]?.GetValue<string>() is not { } ent) continue;
            if (!declaredByEntity.TryGetValue(ent, out var cols) || cols.Count == 0) continue;

            config ??= new JsonObject();
            config["columns"] = new JsonArray(cols.Select(c => (JsonNode)c!).ToArray());
            v["config"] = config;
        }
    }

    // Ordering of view types within an entity's page tabs (Overview → list → board → time views).
    private static readonly Dictionary<string, int> ViewTabOrder = new()
    {
        ["dashboard"] = 0, ["table"] = 1, ["kanban"] = 2, ["calendar"] = 3, ["timeline"] = 4,
    };

    /// <summary>
    /// Synthesize the Page/Block/Tabs layout (<c>manifest.pages</c>) the renderer consumes, from the
    /// views grouped by entity. Each entity with ≥1 non-detail view becomes a page; multiple views
    /// become a <c>tabs</c> block (Overview/list/board/…), a lone view a single <c>view</c> block.
    /// Detail views are the record drill-in, not tabs. This is the compiler-side default until the AI
    /// authors pages directly (schema v1.1). Blocks are recursive so richer layouts slot in later.
    /// </summary>
    private static void SynthesizePages(JsonObject manifest)
    {
        var entities = Arr(manifest["entities"]).OfType<JsonObject>().ToList();
        var subordinate = SubordinateKeys(manifest);

        var viewsByEntity = new Dictionary<string, List<JsonObject>>();
        foreach (var v in Arr(manifest["views"]).OfType<JsonObject>())
        {
            if (v["type"]?.GetValue<string>() == "detail") continue;
            if (v["entity"]?.GetValue<string>() is not { } ent) continue;
            if (!viewsByEntity.TryGetValue(ent, out var list)) viewsByEntity[ent] = list = new();
            list.Add(v);
        }

        var pages = new JsonArray();
        foreach (var entity in entities)
        {
            if (entity["key"]?.GetValue<string>() is not { } ek) continue;
            if (subordinate.Contains(ek)) continue;   // no page — embedded in its parent's detail
            if (GetStr(entity, "kind") == "settings")   // singleton config → a settings-form surface, not a list
            {
                var sp = new JsonObject
                {
                    ["key"] = "page_" + ek,
                    ["label"] = GetStr(entity, "label") ?? ek,
                    ["entity"] = ek,
                    ["blocks"] = new JsonArray(new JsonObject { ["kind"] = "settings", ["entity"] = ek }),
                };
                if (GetStr(entity, "icon") is { } sic) sp["icon"] = sic;
                pages.Add(sp);
                continue;
            }
            if (!viewsByEntity.TryGetValue(ek, out var vs) || vs.Count == 0) continue;

            // Stable-sort by view-type priority, keeping declaration order within a type.
            var ordered = vs.Select((v, i) => (v, i))
                .OrderBy(t => ViewTabOrder.GetValueOrDefault(t.v["type"]?.GetValue<string>() ?? "", 9))
                .ThenBy(t => t.i)
                .Select(t => t.v).ToList();

            JsonArray blocks;
            if (ordered.Count == 1)
            {
                blocks = new JsonArray(ViewBlock(ordered[0]));
            }
            else
            {
                var tabs = new JsonArray();
                foreach (var v in ordered)
                    tabs.Add(new JsonObject
                    {
                        ["key"] = v["key"]?.GetValue<string>(),
                        ["label"] = v["label"]?.GetValue<string>() ?? v["key"]?.GetValue<string>(),
                        ["blocks"] = new JsonArray(ViewBlock(v)),
                    });
                blocks = new JsonArray(new JsonObject { ["kind"] = "tabs", ["tabs"] = tabs });
            }

            var page = new JsonObject
            {
                ["key"] = "page_" + ek,
                ["label"] = entity["labelPlural"]?.GetValue<string>() ?? entity["label"]?.GetValue<string>() ?? ek,
                ["entity"] = ek,
                ["blocks"] = blocks,
            };
            if (entity["icon"]?.GetValue<string>() is { } icon) page["icon"] = icon;
            pages.Add(page);
        }
        if (pages.Count > 0) manifest["pages"] = pages;
    }

    private static JsonObject ViewBlock(JsonObject v) =>
        new() { ["kind"] = "view", ["view"] = v["key"]?.GetValue<string>() };

    // ---- parent/child IA (subordination + composed detail) -------------------------------------

    // Forms child roles are subordinate by archetype; the formTemplate detail (Design/Take/Results)
    // already surfaces them, so they are suppressed from nav but NOT re-embedded as child-blocks.
    // The vocabulary itself lives in Cordango.Definition: DesignDefaults and the candidate smoke
    // checks need the same answer to "is this entity a subject of its own", and a second copy here is
    // how they would drift apart — helpdesk's three intake entities are the case that catches it.
    private static readonly IReadOnlySet<string> SubordinateFormRoles = Archetypes.SubordinateFormRoles;

    /// <summary>An entity is subordinate (embedded in its parent, no top-level nav/page) when it declares
    /// or the compiler infers an <c>ownedBy</c>, or it plays a child role in the Forms archetype.</summary>
    private static bool IsSubordinateEntity(JsonObject entity) => Archetypes.IsSubordinate(entity);

    private static HashSet<string> SubordinateKeys(JsonObject manifest) =>
        Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(IsSubordinateEntity)
            .Select(e => GetStr(e, "key")).Where(k => k is not null).Select(k => k!)
            .ToHashSet();

    /// <summary>Fill in <c>ownedBy</c> for entities the AI didn't tag, conservatively: only an entity the
    /// AI gave NO standalone view to (so it never meant it as a browsable list), with a single parent (one
    /// incoming oneToMany, or a lone required local reference) and which nothing else depends on, is treated
    /// as that parent's child. An entity with its own view, or with an ambiguous parent, stays primary.</summary>
    private static void InferSubordination(JsonObject manifest)
    {
        var entities = Arr(manifest["entities"]).OfType<JsonObject>().ToList();
        var keys = entities.Select(e => GetStr(e, "key")).Where(k => k is not null).Select(k => k!).ToHashSet();
        var relations = Arr(manifest["relations"]).OfType<JsonObject>().ToList();

        // entities the AI surfaced with a standalone (non-detail) view — it intends these as primary.
        var hasOwnView = Arr(manifest["views"]).OfType<JsonObject>()
            .Where(v => GetStr(v, "type") != "detail")
            .Select(v => GetStr(v, "entity")).Where(e => e is not null).Select(e => e!).ToHashSet();

        // who references whom (local references only) — keeps shared/hub entities primary.
        var referencedBy = new Dictionary<string, HashSet<string>>();
        foreach (var e in entities)
        {
            var ek = GetStr(e, "key"); if (ek is null) continue;
            foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
            {
                if (GetStr(f, "type") != "reference" || GetStr(f, "targetApp") is { Length: > 0 }) continue;
                var tgt = GetStr(f, "targetEntity");
                if (tgt is null || tgt == ek) continue;
                (referencedBy.TryGetValue(tgt, out var s) ? s : referencedBy[tgt] = new()).Add(ek);
            }
        }

        foreach (var e in entities)
        {
            var ek = GetStr(e, "key"); if (ek is null) continue;
            if (e["ownedBy"] is JsonObject) continue;                          // explicit — respect it
            var role = GetStr(e, "role");
            if (role == "formTemplate" || (role is { } rr && SubordinateFormRoles.Contains(rr))) continue;
            if (hasOwnView.Contains(ek)) continue;                             // AI intends it primary

            var links = new List<(string parent, string via)>();
            foreach (var rel in relations)
            {
                if (GetStr(rel, "type") != "oneToMany" || GetStr(rel, "toEntity") != ek) continue;
                var parent = GetStr(rel, "fromEntity");
                var via = GetStr(rel, "inverseField");
                if (parent is not null && parent != ek && keys.Contains(parent) && via is not null && HasLocalRefTo(e, via, parent))
                    links.Add((parent, via));
            }
            if (links.Count == 0)   // fallback: a required local reference to a single parent
                foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
                {
                    if (GetStr(f, "type") != "reference" || GetStr(f, "targetApp") is { Length: > 0 }) continue;
                    if (f["required"]?.GetValue<bool>() != true) continue;
                    var tgt = GetStr(f, "targetEntity");
                    if (tgt is not null && tgt != ek && keys.Contains(tgt)) links.Add((tgt, GetStr(f, "key")!));
                }

            var parents = links.Select(l => l.parent).Distinct().ToList();
            if (parents.Count != 1) continue;                                  // 0 or ambiguous → primary
            var refs = referencedBy.GetValueOrDefault(ek);
            if (refs is not null && refs.Any(r => r != parents[0])) continue;  // depended on elsewhere → primary
            var chosen = links.First(l => l.parent == parents[0]);
            e["ownedBy"] = new JsonObject { ["parent"] = chosen.parent, ["via"] = chosen.via, ["inferred"] = true };
        }
    }

    /// <summary>A lookup entity carrying more business fields than this is a real subject, not config.</summary>
    private const int ConfigMaxFields = 6;

    /// <summary>Tag LOOKUP/reference entities as <c>kind:'config'</c> when the AI didn't. A config entity is a
    /// small list other entities point AT (territories, product types, categories, risk tiers, partner types).
    /// It stays a browsable, editable list — but it belongs in a grouped Configuration area, not as a
    /// top-level nav entry beside the app's real subjects. Conservative: something must reference it, it must
    /// carry no lifecycle (no process, no role:'status' field), stay small, and never swallow every entity.</summary>
    private static void InferConfigEntities(JsonObject manifest)
    {
        var entities = Arr(manifest["entities"]).OfType<JsonObject>().ToList();
        var subordinate = SubordinateKeys(manifest);
        var processEntities = Arr(manifest["processes"]).OfType<JsonObject>()
            .Select(p => GetStr(p, "entity")).Where(x => x is not null).Select(x => x!).ToHashSet();
        // An entity a page navigates BY (`navSource`) is a top-level subject the user browses BETWEEN —
        // a workspace of things (projects/boards/spaces), never a lookup list. Small + referenced would
        // otherwise mis-classify it as config and bury its page in the Configuration group.
        var navSourceEntities = Arr(manifest["pages"]).OfType<JsonObject>()
            .Where(p => p["navSource"] is JsonObject)
            .Select(p => GetStr(p, "entity")).Where(x => x is not null).Select(x => x!).ToHashSet();

        // who references whom (local references only) — a lookup is something OTHERS point at.
        var referencedBy = new Dictionary<string, HashSet<string>>();
        foreach (var e in entities)
        {
            var ek = GetStr(e, "key"); if (ek is null) continue;
            foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
            {
                if (GetStr(f, "type") != "reference" || GetStr(f, "targetApp") is { Length: > 0 }) continue;
                var tgt = GetStr(f, "targetEntity");
                if (tgt is null || tgt == ek) continue;
                (referencedBy.TryGetValue(tgt, out var s) ? s : referencedBy[tgt] = new()).Add(ek);
            }
        }

        var candidates = new List<JsonObject>();
        foreach (var e in entities)
        {
            var ek = GetStr(e, "key"); if (ek is null) continue;
            if (GetStr(e, "kind") is { Length: > 0 }) continue;          // explicit intent — respect it
            if (navSourceEntities.Contains(ek)) continue;                // navigated BY → a primary subject
            if (subordinate.Contains(ek)) continue;                      // already embedded in a parent
            if (GetStr(e, "role") is { Length: > 0 }) continue;          // archetype roles (forms) stay as-is
            if (processEntities.Contains(ek)) continue;                  // has a lifecycle → a real subject
            if (!referencedBy.TryGetValue(ek, out var refs) || refs.Count == 0) continue;   // nothing points at it
            var business = Arr(e["fields"]).OfType<JsonObject>()
                .Where(f => f["system"]?.GetValue<bool>() != true).ToList();
            if (business.Count > ConfigMaxFields) continue;               // too rich to be a lookup
            if (business.Any(f => GetStr(f, "role") == "status")) continue;   // carries a lifecycle
            candidates.Add(e);
        }

        // An app needs at least one primary subject — never classify every navigable entity as config.
        var navigable = entities.Count(e => !subordinate.Contains(GetStr(e, "key") ?? ""));
        if (candidates.Count >= navigable) return;
        foreach (var e in candidates) e["kind"] = "config";
    }

    /// <summary>Stamp <c>page.group="config"</c> for pages whose entity is config/settings, so the shell can
    /// collapse them into ONE "Configuration" section. Runs after pages exist, so it applies to BOTH
    /// AI-authored layouts and the compiler's synthesized page-per-entity fallback.</summary>
    private static void TagConfigPages(JsonObject manifest)
    {
        var configKeys = Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(e => GetStr(e, "kind") is "config" or "settings")
            .Select(e => GetStr(e, "key")).Where(k => k is not null).Select(k => k!).ToHashSet();
        if (configKeys.Count == 0) return;
        foreach (var p in Arr(manifest["pages"]).OfType<JsonObject>())
            if (GetStr(p, "entity") is { } pe && configKeys.Contains(pe)) p["group"] = "config";
    }

    private static bool HasLocalRefTo(JsonObject entity, string fieldKey, string parent) =>
        Arr(entity["fields"]).OfType<JsonObject>().Any(f =>
            GetStr(f, "key") == fieldKey && GetStr(f, "type") == "reference" && GetStr(f, "targetEntity") == parent);

    /// <summary>Synthesize a record-detail block tree per PRIMARY entity. Business fields are partitioned by
    /// their <c>group</c> label into an ungrouped Overview plus one area per named group; subordinate children
    /// render as typed child-blocks (feed/checklist/lineItems inline with the Overview, table as its own area),
    /// and non-subordinate oneToMany children as related tables. With ≥3 areas the detail becomes a tab strip
    /// (Overview | Address | Bank | … | related lists); with ≤2 it stacks (named groups as titled sections).
    /// formTemplate keeps its archetype detail (Design/Take/Results tabs), so it is skipped here.</summary>
    private static void SynthesizeDetails(JsonObject manifest)
    {
        var entities = Arr(manifest["entities"]).OfType<JsonObject>().ToList();
        var byKey = entities.Where(e => GetStr(e, "key") is not null).ToDictionary(e => GetStr(e, "key")!, e => e);
        var relations = Arr(manifest["relations"]).OfType<JsonObject>().ToList();
        var processEntities = Arr(manifest["processes"]).OfType<JsonObject>()
            .Select(p => GetStr(p, "entity")).Where(x => x is not null).Select(x => x!).ToHashSet();
        var headerCommands = HeaderCommandsByEntity(manifest);

        foreach (var e in entities)
        {
            var ek = GetStr(e, "key"); if (ek is null) continue;
            if (e["detail"] is JsonObject) continue;                 // respect an existing/authored layout
            if (IsSubordinateEntity(e)) continue;                    // children render inside their parent
            if (GetStr(e, "role") == "formTemplate") continue;       // forms detail owns its tabs
            if (GetStr(e, "kind") == "settings") continue;           // singleton config renders as a settings form

            // Partition business (non-system) fields: ungrouped -> Overview; grouped -> one area per group
            // label (groups ordered by first appearance, field keys in declaration order within a group).
            var overviewKeys = new JsonArray();
            var groupOrder = new List<string>();
            var groupKeys = new Dictionary<string, JsonArray>();
            foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
            {
                if (f["system"]?.GetValue<bool>() == true) continue;
                var fk = GetStr(f, "key"); if (fk is null) continue;
                var g = GetStr(f, "group");
                if (string.IsNullOrWhiteSpace(g)) { overviewKeys.Add(fk); continue; }
                if (!groupKeys.TryGetValue(g, out var arr)) { groupKeys[g] = arr = new JsonArray(); groupOrder.Add(g); }
                arr.Add(fk);
            }

            // Children: subordinate (typed, inline create) split into inline (feed/checklist/lineItems, shown
            // with the header) vs its own area (table); non-subordinate oneToMany -> related table area.
            var inlineChildren = new List<JsonObject>();
            var childAreas = new List<(string label, JsonObject block)>();
            foreach (var c in entities)
            {
                if (c["ownedBy"] is not JsonObject ob || GetStr(ob, "parent") != ek) continue;
                var via = GetStr(ob, "via"); if (via is null) continue;
                var childType = ChildTypeFor(c);
                var label = GetStr(c, "labelPlural") ?? GetStr(c, "label") ?? GetStr(c, "key")!;
                var block = new JsonObject
                {
                    ["kind"] = "child",
                    ["entity"] = GetStr(c, "key"),
                    ["via"] = via,
                    ["childType"] = childType,
                    ["subordinate"] = true,
                    ["inlineCreate"] = true,
                    ["label"] = label,
                };
                if (childType is "feed" or "checklist" or "lineItems") inlineChildren.Add(block);
                else childAreas.Add((label, block));
            }
            foreach (var rel in relations)
            {
                if (GetStr(rel, "type") != "oneToMany" || GetStr(rel, "fromEntity") != ek) continue;
                var to = GetStr(rel, "toEntity"); var via = GetStr(rel, "inverseField");
                if (to is null || via is null || !byKey.TryGetValue(to, out var child) || IsSubordinateEntity(child)) continue;
                var label = GetStr(rel, "label") ?? GetStr(child, "labelPlural") ?? to;
                childAreas.Add((label, new JsonObject
                {
                    ["kind"] = "child",
                    ["entity"] = to,
                    ["via"] = via,
                    ["childType"] = "table",
                    ["subordinate"] = false,
                    ["inlineCreate"] = false,
                    ["label"] = label,
                }));
            }

            // Assemble areas: Overview (ungrouped fields + inline children), one per named group, one per child table.
            var areas = new List<(string label, string type, JsonArray blocks)>();
            var overviewBlocks = new JsonArray();
            if (overviewKeys.Count > 0)
                overviewBlocks.Add(new JsonObject { ["kind"] = "fields", ["fields"] = overviewKeys });
            foreach (var ic in inlineChildren) overviewBlocks.Add(ic);
            if (overviewBlocks.Count > 0) areas.Add(("Overview", "overview", overviewBlocks));
            foreach (var g in groupOrder)
                areas.Add((g, "group", new JsonArray(new JsonObject { ["kind"] = "fields", ["fields"] = groupKeys[g] })));
            foreach (var (label, block) in childAreas)
                areas.Add((label, "child", new JsonArray(block)));

            JsonArray blocks;
            if (areas.Count == 0)
                blocks = new JsonArray(new JsonObject { ["kind"] = "fields" });   // nothing to group — bare grid
            else if (areas.Count >= 3)
            {
                var tabs = new JsonArray();   // enough distinct areas to warrant a tab strip
                foreach (var (label, _, b) in areas)
                    tabs.Add(new JsonObject { ["label"] = label, ["blocks"] = b });
                blocks = new JsonArray(new JsonObject { ["kind"] = "tabs", ["tabs"] = tabs });
            }
            else
            {
                // 1-2 areas: stack. Named groups get a titled section; Overview and child tables render bare.
                blocks = new JsonArray();
                foreach (var (label, type, b) in areas)
                {
                    if (type == "group")
                        blocks.Add(new JsonObject { ["kind"] = "section", ["label"] = label, ["blocks"] = b });
                    else
                        foreach (var blk in b) blocks.Add(blk!.DeepClone());
                }
            }

            // Every primary detail leads with a hub header (identity + status + key facts) — the
            // design pass authors richer ones; this is the fallback that upgrades old apps on rebuild.
            blocks.Insert(0, SynthesizeHub(e, headerCommands.GetValueOrDefault(ek) ?? new List<string>()));
            // A process-governed entity gets the state stepper right under the hub.
            if (processEntities.Contains(ek))
                blocks.Insert(1, new JsonObject { ["kind"] = "process" });

            e["detail"] = new JsonObject { ["blocks"] = blocks };
        }
    }

    /// <summary>Default hub header for a synthesized detail: title = displayField, status = the
    /// role:'status' field, facts = the first 4 ungrouped scannable fields (no longtext, and not
    /// the title/status themselves).</summary>
    private static JsonObject SynthesizeHub(JsonObject entity, IEnumerable<string> headerCommands)
    {
        var title = GetStr(entity, "displayField");
        string? status = null;
        var facts = new JsonArray();
        foreach (var f in Arr(entity["fields"]).OfType<JsonObject>())
        {
            if (f["system"]?.GetValue<bool>() == true) continue;
            if (GetStr(f, "role") == "status") { status ??= GetStr(f, "key"); continue; }
            var fk = GetStr(f, "key");
            if (fk is null || fk == title || facts.Count >= 4) continue;
            if (GetStr(f, "type") == "longtext") continue;
            if (!string.IsNullOrWhiteSpace(GetStr(f, "group"))) continue;
            facts.Add(fk);
        }
        var hub = new JsonObject { ["kind"] = "hub" };
        if (title is not null) hub["title"] = title;
        if (status is not null) hub["status"] = status;
        if (facts.Count > 0) hub["facts"] = facts;
        // Domain command buttons lead the header, then the built-in edit/delete.
        var actions = new JsonArray();
        foreach (var ck in headerCommands) actions.Add(ck);
        actions.Add("edit"); actions.Add("delete");
        hub["actions"] = actions;
        return hub;
    }

    // Pick a child-block by the child entity's field shape; the AI can override via ownedBy.as.
    // feed/checklist are DIRECT-WRITE composers: they create a record from ONE field (+ the parent
    // ref), so they are only viable when that one field covers every required field — otherwise
    // every Post 400s ("'event_type' is required") and the surface is unusable. Observed live on a
    // people app whose lifecycle-event child had a longtext AND required type/date fields. Such
    // children fall through to table/lineItems, whose Add button opens the full record form
    // (parent ref prefilled).
    private static string ChildTypeFor(JsonObject child)
    {
        if (GetStr(child["ownedBy"], "as") is { } forced) return forced;
        var fields = Arr(child["fields"]).OfType<JsonObject>().Where(f => f["system"]?.GetValue<bool>() != true).ToList();
        bool Has(params string[] types) => fields.Any(f => types.Contains(GetStr(f, "type")));

        var via = GetStr(child["ownedBy"], "via");
        // The one field each composer writes (bodyField/titleField).
        var bodyField = fields.FirstOrDefault(f => GetStr(f, "type") == "longtext")
                     ?? fields.FirstOrDefault(f => GetStr(f, "type") == "text");
        var titleField = fields.FirstOrDefault(f => GetStr(f, "key") == GetStr(child, "displayField"))
                      ?? fields.FirstOrDefault(f => GetStr(f, "type") == "text");
        bool ComposerCovers(JsonObject? written) => fields.All(f =>
            f["required"]?.GetValue<bool>() != true
            || f["readOnly"]?.GetValue<bool>() == true
            || GetStr(f, "key") == via
            || (written is not null && GetStr(f, "key") == GetStr(written, "key")));

        if (Has("money")) return "lineItems";
        if (Has("longtext") && ComposerCovers(bodyField)) return "feed";
        if (Has("boolean") && fields.Count <= 4 && ComposerCovers(titleField)) return "checklist";
        return "table";
    }

    // ---- processes + commands (behavior) --------------------------------------------------------

    /// <summary>For each process, rewrite its status field's <c>options</c> FROM the process states
    /// (order, labels, colors), stamp the field's <c>default</c> to the initial state, and mark the
    /// field <c>governedBy</c> the process — one source of truth for the states in the manifest.</summary>
    private static void CanonicalizeProcesses(JsonObject manifest)
    {
        var processes = Arr(manifest["processes"]);
        if (processes.Count == 0) return;
        var byKey = Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(e => GetStr(e, "key") is not null).ToDictionary(e => GetStr(e, "key")!, e => e);
        foreach (var p in processes.OfType<JsonObject>())
        {
            var ent = GetStr(p, "entity"); var sf = GetStr(p, "stateField");
            if (ent is null || sf is null || !byKey.TryGetValue(ent, out var entity)) continue;
            var field = Arr(entity["fields"]).OfType<JsonObject>().FirstOrDefault(f => GetStr(f, "key") == sf);
            if (field is null) continue;

            var existingColor = new Dictionary<string, string>();
            foreach (var o in Arr(field["options"]).OfType<JsonObject>())
                if (GetStr(o, "value") is { } ov && GetStr(o, "color") is { } oc) existingColor[ov] = oc;

            var opts = new JsonArray();
            foreach (var s in Arr(p["states"]).OfType<JsonObject>())
            {
                var sk = GetStr(s, "key"); if (sk is null) continue;
                var color = GetStr(s, "color") ?? existingColor.GetValueOrDefault(sk) ?? SemanticColor(sk) ?? Palette[opts.Count % Palette.Length];
                var opt = new JsonObject { ["value"] = sk, ["label"] = GetStr(s, "label") ?? sk, ["color"] = color };
                // The state's lifecycle phase rides along on the option — the manifest's options array
                // is the ONE transport the renderer reads status semantics from (done affordance,
                // late-start/overdue colorization), process-governed or not.
                if (GetStr(s, "phase") is { } phase) opt["phase"] = phase;
                opts.Add(opt);
            }
            field["options"] = opts;
            ApplyProcessEntry(p, field);
            if (GetStr(p, "key") is { } pk) field["governedBy"] = pk;
        }
    }

    /// <summary>Lower the process's entry rule onto the state field. A plain <c>initialState</c> is the
    /// field's <c>default</c>; CONDITIONAL entry (<c>{ rules:[{when,state}], fallback }</c>) becomes the
    /// field's <c>initial</c> array plus a <c>default</c> of the fallback.
    /// <para>Lowering rather than adding a runtime path is the point: <c>AppDataService</c> already
    /// evaluates <c>initial</c> at insert, top-to-bottom, first match wins, with a one-relation hop for
    /// a <c>path</c> guard — which is exactly conditional entry's semantics. So the DEFINITION gains a
    /// concept and the runtime gains nothing to get wrong. Authors may no longer write <c>initial</c> on
    /// a governed field (the gate rejects it); this is the only thing that writes it there.</para></summary>
    private static void ApplyProcessEntry(JsonObject p, JsonObject field)
    {
        if (GetStr(p, "initialState") is { } init) { field["default"] = init; return; }
        if (p["initialState"] is not JsonObject entry) return;
        if (GetStr(entry, "fallback") is { } fb) field["default"] = fb;

        var rules = new JsonArray();
        foreach (var r in Arr(entry["rules"]).OfType<JsonObject>())
        {
            if (r["when"] is not JsonObject when || GetStr(r, "state") is not { } state) continue;
            rules.Add(new JsonObject { ["when"] = when.DeepClone(), ["value"] = state });
        }
        if (rules.Count > 0) field["initial"] = rules;
    }

    /// <summary>Synthesize a command for every process transition that doesn't name one (so every legal
    /// state move has an invokable button), then stamp <c>transition</c>/<c>process</c> markers on the
    /// command that backs each transition. Authored commands always win.</summary>
    private static void SynthesizeCommands(JsonObject manifest)
    {
        var processes = Arr(manifest["processes"]);
        if (processes.Count == 0) return;
        var commands = manifest["commands"] as JsonArray ?? new JsonArray();
        var existing = new HashSet<string>();                 // "entity|key"
        foreach (var c in commands.OfType<JsonObject>())
            if (GetStr(c, "entity") is { } ce && GetStr(c, "key") is { } ck) existing.Add(ce + "|" + ck);

        foreach (var p in processes.OfType<JsonObject>())
        {
            var ent = GetStr(p, "entity"); if (ent is null) continue;
            var terminalNeg = Arr(p["states"]).OfType<JsonObject>()
                .Where(s => s["terminal"]?.GetValue<bool>() == true && SemanticColor(GetStr(s, "key") ?? "") == "#ef4444")
                .Select(s => GetStr(s, "key")).Where(k => k is not null).Select(k => k!).ToHashSet();

            foreach (var t in Arr(p["transitions"]).OfType<JsonObject>())
            {
                if (!ProcessCommands.IsSynthesized(GetStr(t, "command"))) continue;  // authored wins
                var tkey = GetStr(t, "key"); if (tkey is null) continue;
                // ProcessCommands, not a local string: the GATE registers this same key so a block
                // may reference the button, and two copies of a naming convention agree until one
                // of them changes.
                var ckey = ProcessCommands.SynthesizedKey(ent, tkey); var baseKey = ckey; var n = 2;
                while (existing.Contains(ent + "|" + ckey)) ckey = baseKey + "_" + n++;
                existing.Add(ent + "|" + ckey);

                var label = GetStr(t, "label") ?? Humanize(tkey);
                var cmd = new JsonObject
                {
                    ["key"] = ckey,
                    ["label"] = label,
                    ["entity"] = ent,
                    ["placements"] = new JsonArray("recordHeader"),
                    ["effects"] = new JsonArray(),
                    ["synthesized"] = true,
                };
                if (GetStr(t, "to") is { } to && terminalNeg.Contains(to))
                {
                    cmd["style"] = "danger";
                    cmd["confirm"] = new JsonObject { ["title"] = label + "?", ["tone"] = "danger" };
                }
                if (Arr(t["requiredFields"]) is { Count: > 0 } rf)
                    cmd["input"] = new JsonObject { ["fields"] = rf.DeepClone(), ["required"] = rf.DeepClone() };

                t["command"] = ckey;
                commands.Add(cmd);
            }
        }

        // Stamp the transition/process markers on whichever command backs each transition (authored or
        // synthesized) so the runtime knows the command performs a state move.
        var index = commands.OfType<JsonObject>()
            .Where(c => GetStr(c, "entity") is not null && GetStr(c, "key") is not null)
            .ToDictionary(c => GetStr(c, "entity") + "|" + GetStr(c, "key"), c => c);
        foreach (var p in processes.OfType<JsonObject>())
        {
            var ent = GetStr(p, "entity"); var pk = GetStr(p, "key"); if (ent is null) continue;
            foreach (var t in Arr(p["transitions"]).OfType<JsonObject>())
                if (GetStr(t, "command") is { } tc && index.TryGetValue(ent + "|" + tc, out var cmd))
                {
                    cmd["transition"] = GetStr(t, "key");
                    if (pk is not null) cmd["process"] = pk;
                }
        }

        if (commands.Count > 0) manifest["commands"] = commands;
    }

    /// <summary>
    /// Force a board to read-only when dragging its cards could not legally do anything.
    ///
    /// <para>The design pass marks every board <c>interactive</c> (it is also the default when the
    /// key is omitted), which offers drag on fields the runtime would reject: a system stamp, a
    /// read-only or auto field, a value only a command may set. Derive it from the field instead of
    /// asking.</para>
    ///
    /// <para><b>A process-governed status is NOT one of those, and treating it as one was a stale
    /// rule.</b> It was written when a drop wrote the grouping field directly, so offering drag over
    /// a status the process guards would have promised a move the server refuses. Both renderers
    /// have since learned the difference: a drop over a governed field looks up the declared
    /// TRANSITION, runs its command where it has one, and refuses an illegal move by name. Keeping
    /// the downgrade meant the language's own description of a process board — "dragging a card
    /// between columns runs the matching transition command" — was something no definition could
    /// actually ask for, because this rewrote every one of them to read-only on the way past.</para>
    /// </summary>
    private static void ResolveBoardInteraction(JsonObject manifest)
    {
        var fieldsByEntity = Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(e => GetStr(e, "key") is not null)
            .ToDictionary(e => GetStr(e, "key")!, e => Arr(e["fields"]).OfType<JsonObject>().ToList());

        foreach (var v in Arr(manifest["views"]).OfType<JsonObject>())
        {
            if (GetStr(v, "type") != "kanban") continue;
            if (v["config"] is not JsonObject cfg) continue;
            if (GetStr(cfg, "interaction") == "visualization") continue;   // already read-only
            if (GetStr(v, "entity") is not { } ek || !fieldsByEntity.TryGetValue(ek, out var fields)) continue;

            var gk = GetStr(cfg, "groupByField");
            var f = fields.FirstOrDefault(x => GetStr(x, "key") == gk);
            if (f is null) continue;

            // Nothing writes these, transition or not.
            var editable = f["system"]?.GetValue<bool>() != true
                        && f["readOnly"]?.GetValue<bool>() != true
                        && f["auto"] is null;

            // A process is a write path, so a governed field is draggable. Without one,
            // `setByCommand` says the only write path is a command and a drag has none.
            var governed = f["governedBy"] is not null;
            var writable = editable && (governed || f["setByCommand"]?.GetValue<bool>() != true);

            if (!writable) cfg["interaction"] = "visualization";
        }
    }

    /// <summary>
    /// Resolve every opted-in entity's <c>calendar</c> flag into the fields it means, and stamp the
    /// answer into the manifest as <c>calendar</c> — replacing the flag or the partial object the
    /// author wrote with a complete one.
    ///
    /// <para><b>Derivation belongs here, not in the runtime.</b> The union endpoint answers a request
    /// per person per window; re-deriving "which field says who" on each of those would make every
    /// read depend on a belief that could drift from what the Gate checked. The compiler writes the
    /// BUILT artifact and never the definition, and an authored key is carried through untouched —
    /// which is the line <c>ResolveBoardInteraction</c> once crossed, disabling every process kanban
    /// by rewriting definitions from an outgrown belief about the renderer.</para>
    ///
    /// <para>A flag that will not resolve is dropped rather than half-stamped. The Gate refuses those
    /// with the reason, so reaching this with an unresolvable one means the gate was skipped, and a
    /// partial binding downstream is worse than no calendar: the entity would appear in the union
    /// with no date or nobody's name on it.</para>
    /// </summary>
    private static void ResolveCalendar(JsonObject manifest)
    {
        var entities = Arr(manifest["entities"]);
        foreach (var e in entities.OfType<JsonObject>())
        {
            if (!CalendarResolver.IsOptedIn(e)) { e.Remove("calendar"); continue; }
            var (binding, _) = CalendarResolver.Resolve(entities, e);
            if (binding is null) e.Remove("calendar");
            else e["calendar"] = binding.ToJson();
        }
    }

    /// <summary>Stamp <c>readOnly:true</c> on every computed field. The value is derived (an expr
    /// over the record's own fields, or a rollup over referencing records) and recomputed by the
    /// data service on every write — a client-supplied value would be overwritten anyway, so no
    /// form, cell or board may collect one. RecordForm/ValidateAndCopy already honor readOnly.</summary>
    private static void FlagComputedFields(JsonObject manifest)
    {
        foreach (var e in Arr(manifest["entities"]).OfType<JsonObject>())
            foreach (var f in Arr(e["fields"]).OfType<JsonObject>())
                if (f["computed"] is JsonObject)
                    f["readOnly"] = true;
    }

    /// <summary>
    /// Stamp <c>setByCommand:true</c> on every entity field a command collects via
    /// <c>input.fields</c>. Such a field is the command's to write (a decline reason is set when you
    /// decline, an assignee when you assign) — it must not appear as a freely-editable control on the
    /// generic create OR edit form, nor be inline-editable in a cell/board. The command dialog still
    /// collects it (it reads <c>command.input.fields</c> directly). Runs after SynthesizeCommands so it
    /// sees authored and synthesized commands alike; manifest-only flag, like <c>governedBy</c>.
    ///
    /// <para><b>A field required at create time is never command-owned.</b> The flag says "there is a
    /// better way to set this than the create form", and for a required field there is no other way at
    /// all — the record cannot exist without it. A generated budget app declared
    /// <c>clone_scenario</c> collecting <c>name</c> and <c>case_type</c>, which is perfectly sensible
    /// for a clone, and that stripped both from the New Scenario form: the dialog opened with neither
    /// field on it and refused to save because 'name' is required. Nothing could ever be created (live
    /// 2026-08-05).</para>
    /// </summary>
    private static void FlagCommandOwnedFields(JsonObject manifest)
    {
        var fieldsByEntity = Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(e => GetStr(e, "key") is not null)
            .ToDictionary(e => GetStr(e, "key")!, e => Arr(e["fields"]).OfType<JsonObject>().ToList());

        foreach (var c in Arr(manifest["commands"]).OfType<JsonObject>())
        {
            if (GetStr(c, "entity") is not { } ent || !fieldsByEntity.TryGetValue(ent, out var fields)) continue;
            foreach (var fn in Arr(c["input"]?["fields"]))
            {
                if (fn?.GetValue<string>() is not { } fk) continue;
                if (fields.FirstOrDefault(x => GetStr(x, "key") == fk) is not { } f) continue;
                if (f["system"]?.GetValue<bool>() == true) continue;
                // Required, and not hidden on create: the create form is the only place it can be
                // filled, so a command may ALSO collect it but may not own it.
                if (f["required"]?.GetValue<bool>() == true
                    && f["hideOnCreate"]?.GetValue<bool>() != true) continue;
                f["setByCommand"] = true;
            }
        }
    }

    /// <summary>An editable CELL writes its field straight through, so the field must be one the runtime
    /// would actually accept. The gate already rejects system/readOnly/auto — but `governedBy` /
    /// `setByCommand` are stamped by the compiler AFTER the gate has run, so a status a process owns or a
    /// field a command collects can only be caught here. Same rule as boards: derive it, don't trust it.
    /// A governed/command-owned field moves through its transitions or its command, never by typing in a grid.</summary>
    private static void ResolveCellEditing(JsonObject manifest)
    {
        var fieldsByEntity = Arr(manifest["entities"]).OfType<JsonObject>()
            .Where(e => GetStr(e, "key") is not null)
            .ToDictionary(e => GetStr(e, "key")!, e => Arr(e["fields"]).OfType<JsonObject>().ToList());

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray arr:
                    foreach (var n in arr) Walk(n);
                    return;
                case JsonObject b:
                    if (GetStr(b, "kind") == "cell" && b["editable"]?.GetValue<bool>() == true
                        && GetStr(b, "entity") is { } ek && GetStr(b, "field") is { } fk
                        && fieldsByEntity.TryGetValue(ek, out var fields)
                        && fields.FirstOrDefault(x => GetStr(x, "key") == fk) is { } f
                        && (f["governedBy"] is not null || f["system"]?.GetValue<bool>() == true
                            || f["readOnly"]?.GetValue<bool>() == true || f["auto"] is not null
                            || f["setByCommand"]?.GetValue<bool>() == true))
                        b["editable"] = false;
                    foreach (var k in new[] { "blocks", "tabs", "columns" }) Walk(b[k]);
                    return;
            }
        }

        foreach (var p in Arr(manifest["pages"]).OfType<JsonObject>()) Walk(p["blocks"]);
        foreach (var e in Arr(manifest["entities"]).OfType<JsonObject>())
            if (e["detail"] is JsonObject det) Walk(det["blocks"]);
    }

    /// <summary>Default every command's placement to the record header; a transition command on an
    /// entity whose board is interactive also gets a kanban-card action.</summary>
    private static void ResolvePlacements(JsonObject manifest)
    {
        var commands = Arr(manifest["commands"]);
        if (commands.Count == 0) return;
        var interactiveBoards = new HashSet<string>();
        foreach (var v in Arr(manifest["views"]).OfType<JsonObject>())
        {
            if (GetStr(v, "type") != "kanban") continue;
            if (GetStr(v["config"], "interaction") is null or "interactive" && GetStr(v, "entity") is { } ve)
                interactiveBoards.Add(ve);
        }
        foreach (var c in commands.OfType<JsonObject>())
        {
            if (c["placements"] is not JsonArray pls || pls.Count == 0)
                c["placements"] = pls = new JsonArray("recordHeader");
            if (c["transition"] is not null && GetStr(c, "entity") is { } ent && interactiveBoards.Contains(ent)
                && !pls.OfType<JsonValue>().Any(v => v.GetValue<string>() == "kanbanCard"))
                pls.Add("kanbanCard");
        }
    }

    /// <summary>entity key -> command keys placed on the record header (for hub actions).</summary>
    private static Dictionary<string, List<string>> HeaderCommandsByEntity(JsonObject manifest)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var c in Arr(manifest["commands"]).OfType<JsonObject>())
        {
            if (GetStr(c, "entity") is not { } e || GetStr(c, "key") is not { } k) continue;
            var placements = Arr(c["placements"]);
            var onHeader = placements.Count == 0 || placements.OfType<JsonValue>().Any(v => v.GetValue<string>() == "recordHeader");
            if (!onHeader) continue;
            (map.TryGetValue(e, out var l) ? l : map[e] = new()).Add(k);
        }
        return map;
    }

    private static string Humanize(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? GetStr(JsonNode? n, string prop) =>
        n?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Ensure every entity, every page and the app itself carry a valid icon from the curated
    /// vocabulary: AI-chosen icons are kept when real, junk is replaced via keyword fallback
    /// (<see cref="IconCatalog.Resolve"/>). Also defaults the `presentation` block (launcher tile).
    /// </summary>
    private static void ResolveIcons(JsonObject manifest)
    {
        var iconByEntity = new Dictionary<string, string>();
        foreach (var e in Arr(manifest["entities"]).OfType<JsonObject>())
        {
            var icon = IconCatalog.Resolve(e["icon"]?.GetValue<string>(), "table",
                e["key"]?.GetValue<string>(), e["label"]?.GetValue<string>(), e["labelPlural"]?.GetValue<string>());
            e["icon"] = icon;
            if (e["key"]?.GetValue<string>() is { } key) iconByEntity[key] = icon;
        }

        foreach (var p in Arr(manifest["pages"]).OfType<JsonObject>())
        {
            var entityIcon = p["entity"]?.GetValue<string>() is { } ek ? iconByEntity.GetValueOrDefault(ek) : null;
            p["icon"] = IconCatalog.Resolve(p["icon"]?.GetValue<string>(), entityIcon ?? "view-grid",
                p["key"]?.GetValue<string>(), p["label"]?.GetValue<string>());
        }

        // Launcher tile: presentation {icon, color} always present after a build.
        var pres = manifest["presentation"] as JsonObject ?? new JsonObject();
        pres["icon"] = IconCatalog.Resolve(pres["icon"]?.GetValue<string>(), "apps",
            manifest["key"]?.GetValue<string>(), manifest["name"]?.GetValue<string>(),
            manifest["description"]?.GetValue<string>());
        if (pres["color"]?.GetValue<string>() is not { Length: > 0 })
        {
            var seed = manifest["key"]?.GetValue<string>() ?? manifest["name"]?.GetValue<string>() ?? "app";
            var hash = 0; foreach (var c in seed) hash = (hash * 31 + c) & 0x7fffffff;
            pres["color"] = Palette[hash % Palette.Length];
        }
        manifest["presentation"] = pres;
    }

    // --- helpers ---
    private static JsonArray Arr(JsonNode? node) => node as JsonArray ?? new JsonArray();
    private static JsonNode? Str(JsonNode? node) => node is null ? null : JsonValue.Create(node.GetValue<string>());

    /// <summary>Return a new object with <paramref name="key"/> first, then the rest in order.</summary>
    private static JsonObject Prepend(JsonObject obj, string key, JsonNode value)
    {
        var result = new JsonObject { [key] = value };
        foreach (var kv in obj.ToList())
        {
            obj.Remove(kv.Key);            // detach from old parent before re-adding
            result[kv.Key] = kv.Value;
        }
        return result;
    }
}
