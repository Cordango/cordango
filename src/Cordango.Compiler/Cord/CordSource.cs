// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// One source document: where it files, and what it says.
/// </summary>
/// <param name="Path">Repo-relative, forward-slashed, WITHOUT a syntax extension — a host appends
/// <c>.cord.yaml</c>. The layout is the format; the syntax is not.</param>
public sealed record CordSourceFile(string Path, JsonObject Document);

/// <summary>
/// An application as declarative per-aggregate documents, and back — <b>totally</b>.
///
/// <para><b>Why this exists next to <see cref="CordDocument"/>.</b> That one writes the app as the
/// OPERATIONS that would create it. An operation is a command log, it cannot say anything the
/// authoring vocabulary lacks, and so it reports a raw fragment as unwritable and omits it. Measured
/// over the corpus on 2026-08-13 that meant <b>all 15 reference apps had unwritable pointers and not
/// one could be written to files at all</b> — the entire visual layer (<c>pages</c>, <c>views</c>,
/// every entity's <c>detail</c>/<c>form</c>/<c>peek</c>) lives in the overlay, plus <c>theme</c>,
/// <c>presentation</c>, <c>archetype</c> and <c>relations</c>. A file-backed workspace that cannot
/// open a single real application is not one.</para>
///
/// <para><b>A declaration may say what an operation may not, and that is not a loophole.</b> The rule
/// this appears to bend — no <c>set_raw_json</c> in the tool vocabulary, <see cref="CordApp"/>'s
/// "writing anything is how the semantic model would get bypassed" — governs what a MODEL emits.
/// This is a file writer that never appears in a tool schema. The model still authors only through
/// modelled operations; the files simply stop lying about what the application contains.</para>
///
/// <para><b>It works on the LOWERED document, deliberately.</b> Splitting the App Definition is total
/// by construction and exactly invertible, which is what makes
/// <c>Join(Split(d)) == d</c> a test rather than a hope. The cost is real and worth stating: a
/// Cord-authored screen is written as the block tree it lowers to rather than the compact
/// <c>sections</c> form it was authored in. Writing the semantic form for aggregates that carry no
/// overlay is a later improvement the READER already tolerates, because it reassembles by key rather
/// than by shape — it is not a format change.</para>
///
/// <para><b>No YAML here.</b> Rule 0 keeps this assembly on Definition and the BCL, and
/// <c>BoundaryTests</c> enforces it; a serializer dependency would end the extraction claim.
/// Documents are <see cref="JsonObject"/> and the host picks the syntax — which also keeps the
/// JSONC-versus-YAML question a swap of one small writer rather than a rewrite.</para>
/// </summary>
public static class CordSource
{
    public const string AppFile = "app";

    /// <summary>The record surfaces an entity carries. They are presentation, not domain, and they get
    /// their own files: across the Budget Planner they were 32% of an entity file's bytes and half of
    /// `scenario`'s 632 lines, and somebody hunting for the scenario detail view under <c>views/</c>
    /// reasonably concluded it did not exist.</summary>
    public static readonly string[] Surfaces = ["detail", "peek", "form"];

    /// <summary>Root sections that are their own aggregates rather than app-level facts.</summary>
    private static readonly string[] Sections =
        ["entities", "pages", "views", "roles", "commands", "processes", "workflows"];

    // ---- the name mapping ---------------------------------------------------------------------
    //
    // Every rename between the App Definition and semantic source, as DATA rather than branches:
    // this table is the format's specification, and one spread across twenty conditionals drifts.
    // Documented for humans in examples/semantic/budgetPlanner/README.md, and inverted there by
    // verify.py, which asserts the same round trip this file's tests do.

    private static readonly (string Definition, string Source)[] EntityNames =
        [("key", "entity"), ("labelPlural", "plural"), ("displayField", "display")];

    private static readonly (string Definition, string Source)[] ViewNames =
        [("key", "view"), ("type", "kind"), ("config", "settings")];

    private static readonly (string Definition, string Source)[] PageNames =
        [("key", "screen"), ("blocks", "layout"), ("entity", "subject"),
         ("group", "navigationGroup"), ("navSource", "navigationSource")];

    private static readonly (string Definition, string Source)[] RoleNames = [("key", "role")];

    private static readonly (string Definition, string Source)[] ProcessNames =
        [("key", "lifecycle"), ("initialState", "initial")];

    private static readonly (string Definition, string Source)[] CommandNames = [("key", "action")];

    private static readonly (string Definition, string Source)[] AutomationNames =
        [("key", "automation")];

    // =============================================================================================

    /// <summary>The app as source documents.</summary>
    public static IReadOnlyList<CordSourceFile> Write(CordApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Split(CordLower.Lower(app));
    }

    /// <summary>Source documents back to a <see cref="CordApp"/>.</summary>
    public static (CordApp? App, IReadOnlyList<string> Problems) Read(IEnumerable<CordSourceFile> files)
    {
        var (definition, problems) = Join(files);
        return problems.Count > 0 ? (null, problems) : (CordImport.Import(definition), problems);
    }

    /// <summary>
    /// One App Definition as per-aggregate documents.
    /// </summary>
    public static IReadOnlyList<CordSourceFile> Split(JsonNode? definition)
    {
        var doc = definition as JsonObject ?? [];
        var files = new List<CordSourceFile>();

        // ---- entities, and the surfaces that used to hide inside them ---------------------------
        var order = new JsonObject();
        var entityKeys = new JsonArray();

        foreach (var entity in Items(doc, "entities"))
        {
            var key = Key(entity);
            entityKeys.Add(key);

            var source = Rename(entity, EntityNames);
            foreach (var surface in Surfaces)
            {
                if (!source.Remove(surface, out var body) || body is not JsonObject layout) continue;
                files.AddRange(Surface(key, surface, layout));
            }

            // Fields as a MAP keyed by field key: an insertion in the middle stays a local diff
            // instead of renumbering everything after it, and a field becomes addressable by name.
            if (source.Remove("fields", out var fields)) source["fields"] = ToMap(fields, "key");

            files.Add(Identify(source, "entity", key, $"entities/{key}"));
        }

        // ---- behaviour: a command folds into the transition that fires it ------------------------
        var (processes, folded) = Lifecycles(doc);
        files.AddRange(processes);

        var commandKeys = new JsonArray();
        foreach (var command in Items(doc, "commands"))
        {
            var key = Key(command);
            commandKeys.Add(key);
            if (folded.Contains(key)) continue;
            files.Add(Identify(Rename(command, CommandNames), "action", key, $"workflows/actions/{key}"));
        }

        var automationKeys = new JsonArray();
        foreach (var automation in Items(doc, "workflows"))
        {
            var key = Key(automation);
            automationKeys.Add(key);

            var source = Rename(automation, AutomationNames);
            // `trigger:` rather than a nested object, and never a bare `on:` — unquoted `on` is the
            // boolean true in YAML 1.1, which is how the hand-authored specimen silently lost the
            // trigger from all six of its automations.
            if (source.Remove("trigger", out var trigger) && trigger is JsonObject t)
            {
                if (t.Remove("event", out var evt)) source["trigger"] = evt;
                foreach (var (name, value) in t) source[name] = value?.DeepClone();
            }
            files.Add(Identify(source, "automation", key, $"workflows/automations/{key}"));
        }

        // ---- access -----------------------------------------------------------------------------
        var roleKeys = new JsonArray();
        foreach (var role in Items(doc, "roles"))
        {
            var key = Key(role);
            roleKeys.Add(key);

            var source = Rename(role, RoleNames);
            if (source.Remove("grants", out var grants)) source["grants"] = ToMap(grants, "entity");
            files.Add(Identify(source, "role", key, $"roles/{key}"));
        }

        // ---- screens ----------------------------------------------------------------------------
        var pageKeys = new JsonArray();
        foreach (var page in Items(doc, "pages"))
        {
            var key = Key(page);
            pageKeys.Add(key);
            files.Add(Identify(Rename(page, PageNames), "screen", key, $"views/screens/{key}"));
        }

        var viewKeys = new JsonArray();
        foreach (var view in Items(doc, "views"))
        {
            var key = Key(view);
            viewKeys.Add(key);
            files.Add(Identify(Rename(view, ViewNames), "view", key, $"views/collections/{key}"));
        }

        // ---- the app itself, plus the ORDER a directory cannot express ---------------------------
        var identity = new JsonObject();
        foreach (var (name, value) in doc)
        {
            if (Sections.Contains(name, StringComparer.Ordinal) || value is null) continue;
            identity[name == "key" ? "app" : name] = value.DeepClone();
        }

        // Array order is meaningful — entity order drives navigation, page order drives the shell —
        // and DefinitionHash covers it. A set of files has no order and a Git tree has none either,
        // so without this a round trip rebuilds every value and opens the app on the wrong page.
        order["entities"] = entityKeys;
        order["pages"] = pageKeys;
        order["views"] = viewKeys;
        order["roles"] = roleKeys;
        order["processes"] = new JsonArray([.. Items(doc, "processes").Select(p => (JsonNode)Key(p))]);
        order["commands"] = commandKeys;
        order["workflows"] = automationKeys;
        identity["order"] = order;

        files.Insert(0, new CordSourceFile(AppFile, identity));
        return files;
    }

    /// <summary>
    /// Per-aggregate documents back to one App Definition.
    /// </summary>
    /// <returns>The definition, plus a problem per file that could not be read. Never throws: a
    /// person editing files gets them wrong routinely, and the useful answer names every bad file
    /// rather than only the first.</returns>
    public static (JsonObject Definition, IReadOnlyList<string> Problems) Join(
        IEnumerable<CordSourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var problems = new List<string>();
        var byPath = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!byPath.TryAdd(file.Path, file.Document))
                problems.Add($"{file.Path}: appears more than once");
        }

        if (!byPath.Remove(AppFile, out var identity))
        {
            problems.Add($"{AppFile}: missing — the app's identity and the aggregate order live there");
            return ([], problems);
        }

        var doc = new JsonObject();
        foreach (var (name, value) in identity)
        {
            if (name is "order") continue;
            doc[name == "app" ? "key" : name] = value?.DeepClone();
        }

        var order = identity["order"] as JsonObject ?? [];

        doc["entities"] = Gather(order, "entities", key =>
        {
            var source = Take(byPath, $"entities/{key}", problems);
            if (source is null) return null;

            var entity = Unrename(source, EntityNames);
            if (entity.Remove("fields", out var fields)) entity["fields"] = ToArray(fields, "key");

            foreach (var surface in Surfaces)
            {
                if (Take(byPath, $"views/entities/{key}/{surface}", problems, optional: true)
                    is not { } body) continue;
                body.Remove(surface);
                Unsplit(body, key, byPath, problems);
                entity[surface] = body;
            }
            return entity;
        }, problems);

        var (processes, commands) = Rejoin(order, byPath, problems);
        doc["processes"] = processes;
        doc["commands"] = commands;

        doc["workflows"] = Gather(order, "workflows", key =>
        {
            var source = Take(byPath, $"workflows/automations/{key}", problems);
            if (source is null) return null;

            var automation = Unrename(source, AutomationNames);
            var trigger = new JsonObject();
            if (automation.Remove("trigger", out var evt)) trigger["event"] = evt;
            foreach (var name in new[] { "entity", "field", "cron" })
                if (automation.Remove(name, out var value)) trigger[name] = value;
            if (trigger.Count > 0) automation["trigger"] = trigger;
            return automation;
        }, problems);

        doc["roles"] = Gather(order, "roles", key =>
        {
            var source = Take(byPath, $"roles/{key}", problems);
            if (source is null) return null;

            var role = Unrename(source, RoleNames);
            if (role.Remove("grants", out var grants)) role["grants"] = ToArray(grants, "entity");
            return role;
        }, problems);

        doc["pages"] = Gather(order, "pages", key =>
            Take(byPath, $"views/screens/{key}", problems) is { } p ? Unrename(p, PageNames) : null,
            problems);

        doc["views"] = Gather(order, "views", key =>
            Take(byPath, $"views/collections/{key}", problems) is { } v ? Unrename(v, ViewNames) : null,
            problems);

        // Anything left is a file no `order` entry claimed. Reported rather than dropped: a pulled
        // commit adding an aggregate nobody registered is exactly the case a silent loss would hide.
        foreach (var path in byPath.Keys.OrderBy(p => p, StringComparer.Ordinal))
            problems.Add($"{path}: not listed in the app's `order`, so nothing would load it");

        // An empty section is absent, not empty: `"pages": []` and no pages at all are different
        // documents to DefinitionHash, and the definition never carries the empty form.
        foreach (var section in Sections)
            if (doc[section] is JsonArray { Count: 0 }) doc.Remove(section);

        return (doc, problems);
    }

    // ---- lifecycles: the fold, and its inverse ----------------------------------------------------

    /// <summary>
    /// A process, with each transition carrying the command it fires.
    ///
    /// <para>The transition and the button that runs it are one thing a reader reasons about, so they
    /// are one file. Two details make it reversible: the command's own <c>label</c> is kept only when
    /// it DIFFERS from the transition's — the Budget Planner has "Reopen" on the diagram and "Reopen
    /// for Editing" on the button — and its <c>key</c> only when the command is not named after the
    /// transition. Folding them blindly silently renamed two commands.</para>
    /// </summary>
    private static (List<CordSourceFile> Files, HashSet<string> Folded) Lifecycles(JsonObject doc)
    {
        var files = new List<CordSourceFile>();
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var commands = Items(doc, "commands").ToDictionary(Key, c => c, StringComparer.Ordinal);

        foreach (var process in Items(doc, "processes"))
        {
            var key = Key(process);
            var source = Rename(process, ProcessNames);

            if (source.Remove("states", out var states)) source["states"] = ToMap(states, "key");

            if (source.Remove("transitions", out var transitions) && transitions is JsonArray list)
            {
                var map = new JsonObject();
                foreach (var transition in list.OfType<JsonObject>())
                {
                    var tkey = Key(transition);
                    var body = (JsonObject)transition.DeepClone();
                    body.Remove("key");

                    if (body.Remove("command", out var named)
                        && commands.TryGetValue((string?)named ?? "", out var command))
                    {
                        folded.Add((string?)named ?? "");
                        var action = (JsonObject)command.DeepClone();
                        action.Remove("entity");

                        var label = (string?)action["label"];
                        if (label == (string?)body["label"]) action.Remove("label");
                        if ((string?)action["key"] == tkey) action.Remove("key");

                        body["action"] = action;
                    }
                    map[tkey] = body;
                }
                source["transitions"] = map;
            }

            files.Add(Identify(source, "lifecycle", key, $"workflows/lifecycles/{key}"));
        }

        return (files, folded);
    }

    private static (JsonArray Processes, JsonArray Commands) Rejoin(JsonObject order,
        Dictionary<string, JsonObject> byPath, List<string> problems)
    {
        var recovered = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        var processes = Gather(order, "processes", key =>
        {
            var source = Take(byPath, $"workflows/lifecycles/{key}", problems);
            if (source is null) return null;

            var process = Unrename(source, ProcessNames);
            var entity = (string?)process["entity"];
            if (process.Remove("states", out var states)) process["states"] = ToArray(states, "key");

            if (process.Remove("transitions", out var transitions) && transitions is JsonObject map)
            {
                var list = new JsonArray();
                foreach (var (tkey, value) in map)
                {
                    if (value is not JsonObject body) continue;
                    var transition = (JsonObject)body.DeepClone();
                    transition.Remove("action");

                    var ordered = new JsonObject { ["key"] = tkey };
                    foreach (var (name, v) in transition) ordered[name] = v?.DeepClone();

                    if (body["action"] is JsonObject action)
                    {
                        var ckey = (string?)action["key"] ?? tkey;
                        ordered["command"] = ckey;

                        var command = new JsonObject { ["key"] = ckey };
                        foreach (var (name, v) in action)
                        {
                            if (name == "key") continue;
                            command[name] = v?.DeepClone();
                        }
                        command["entity"] = entity;
                        command["label"] ??= body["label"]?.DeepClone();
                        recovered[ckey] = command;
                    }
                    list.Add(ordered);
                }
                process["transitions"] = list;
            }
            return process;
        }, problems);

        var commands = Gather(order, "commands", key =>
            recovered.TryGetValue(key, out var folded) ? folded
                : Take(byPath, $"workflows/actions/{key}", problems) is { } a
                    ? Unrename(a, CommandNames) : null,
            problems);

        return (processes, commands);
    }

    // ---- surfaces ---------------------------------------------------------------------------------

    /// <summary>One surface file, plus one per KEYED tab of a detail.
    ///
    /// <para>Tabs are files because Cord already models a tab as an addressable aggregate
    /// (<see cref="CordAggregateKinds.Tab"/>, keyed <c>&lt;screen&gt;/&lt;tab&gt;</c>, with its own
    /// upsert and remove) and the co-creation loop reviews one at a time. The <c>tabs</c> block keeps
    /// its position among its siblings and its tab order; only the bodies move out, because a
    /// directory has neither.</para>
    ///
    /// <para>A tab with NO key stays inline. Cord addresses a tab by key, so a keyless one — and
    /// people-hr's employee detail has three — has nothing to address; giving it a positional
    /// filename would invent an identity the model lacks and rename every later file on the next
    /// insertion.</para>
    /// </summary>
    private static IEnumerable<CordSourceFile> Surface(string entity, string surface, JsonObject body)
    {
        var files = new List<CordSourceFile>();
        var doc = (JsonObject)body.DeepClone();

        if (surface == "detail")
        {
            foreach (var block in (doc["blocks"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)block["kind"] != "tabs" || block["tabs"] is not JsonArray tabs) continue;

                var replacement = new JsonArray();
                foreach (var tab in tabs.OfType<JsonObject>())
                {
                    if ((string?)tab["key"] is not { Length: > 0 } key)
                    {
                        replacement.Add(tab.DeepClone());
                        continue;
                    }
                    files.Add(Identify((JsonObject)tab.DeepClone(), "tab", key,
                        $"views/entities/{entity}/tabs/{key}"));
                    replacement.Add(key);
                }
                block["tabs"] = replacement;
            }
        }

        files.Add(Identify(doc, surface, entity, $"views/entities/{entity}/{surface}"));
        return files;
    }

    /// <summary>Re-inline the tab bodies a detail's tab list only names.</summary>
    private static void Unsplit(JsonObject detail, string entity,
        Dictionary<string, JsonObject> byPath, List<string> problems)
    {
        foreach (var block in (detail["blocks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)block["kind"] != "tabs" || block["tabs"] is not JsonArray tabs) continue;

            var restored = new JsonArray();
            foreach (var tab in tabs)
            {
                if (tab is JsonObject inline) { restored.Add(inline.DeepClone()); continue; }
                if (tab is not JsonValue v || !v.TryGetValue<string>(out var key)) continue;

                if (Take(byPath, $"views/entities/{entity}/tabs/{key}", problems) is not { } body)
                    continue;

                var ordered = new JsonObject { ["key"] = key };
                foreach (var (name, value) in body)
                {
                    if (name == "tab") continue;
                    ordered[name] = value?.DeepClone();
                }
                restored.Add(ordered);
            }
            block["tabs"] = restored;
        }
    }

    // ---- plumbing ---------------------------------------------------------------------------------

    private static IEnumerable<JsonObject> Items(JsonObject doc, string section) =>
        (doc[section] as JsonArray ?? []).OfType<JsonObject>();

    private static string Key(JsonObject item) => (string?)item["key"] ?? "";

    private static JsonObject Rename(JsonObject item, (string Definition, string Source)[] names)
    {
        var clone = (JsonObject)item.DeepClone();
        foreach (var (from, to) in names)
            if (clone.Remove(from, out var value)) clone[to] = value;
        return clone;
    }

    private static JsonObject Unrename(JsonObject item, (string Definition, string Source)[] names)
    {
        var clone = (JsonObject)item.DeepClone();
        foreach (var (to, from) in names)
            if (clone.Remove(from, out var value)) clone[to] = value;
        return clone;
    }

    /// <summary>A keyed array as a map, dropping the key property into the map key.</summary>
    private static JsonNode ToMap(JsonNode? array, string keyName)
    {
        var map = new JsonObject();
        foreach (var item in (array as JsonArray ?? []).OfType<JsonObject>())
        {
            var clone = (JsonObject)item.DeepClone();
            clone.Remove(keyName, out var key);
            map[(string?)key ?? ""] = clone;
        }
        return map;
    }

    /// <summary>A map back to a keyed array, restoring the key property FIRST so the identity leads.</summary>
    private static JsonNode ToArray(JsonNode? map, string keyName)
    {
        var array = new JsonArray();
        foreach (var (key, value) in map as JsonObject ?? [])
        {
            var item = new JsonObject { [keyName] = key };
            foreach (var (name, v) in value as JsonObject ?? []) item[name] = v?.DeepClone();
            array.Add(item);
        }
        return array;
    }

    /// <summary>The identity line first, so a file says what it is before it says anything else.</summary>
    private static CordSourceFile Identify(JsonObject doc, string kind, string key, string path)
    {
        var result = new JsonObject { [kind] = key };
        foreach (var (name, value) in doc)
        {
            if (string.Equals(name, kind, StringComparison.Ordinal)) continue;
            result[name] = value?.DeepClone();
        }
        return new CordSourceFile(path, result);
    }

    private static JsonObject? Take(Dictionary<string, JsonObject> byPath, string path,
        List<string> problems, bool optional = false)
    {
        if (byPath.Remove(path, out var doc)) return (JsonObject)doc.DeepClone();
        if (!optional) problems.Add($"{path}: listed in the app's `order` but not present");
        return null;
    }

    private static JsonArray Gather(JsonObject order, string section,
        Func<string, JsonObject?> load, List<string> problems)
    {
        var array = new JsonArray();
        foreach (var entry in order[section] as JsonArray ?? [])
        {
            if (entry is not JsonValue v || !v.TryGetValue<string>(out var key)) continue;
            if (load(key) is { } item) array.Add(item);
        }
        return array;
    }
}
