// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.SourceGen.Common;

/// <summary>What a link produced: one manifest, the names it had to assign, and what it could not do.</summary>
/// <param name="Manifest">The apps as ONE application. Empty when <paramref name="Problems"/> is not.</param>
/// <param name="Names">Every name this link decided, as <c>kind:app:oldKey</c> → new key. Recorded in
/// the build metadata and handed back on the next build so a name, once given, never moves.</param>
public sealed record LinkedWorkspace(
    JsonObject Manifest,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// The apps of a workspace, linked into one application.
///
/// <para><b>Why a link step rather than a rule for authors.</b> The App Definition is the portable
/// artifact: the same two apps have to run on Cordango Platform AND as one generated application. The
/// platform gives each app its own tables behind its own handle, so two apps may each have a
/// <c>dashboard</c> and neither is wrong. A standalone build is one deployment — one database, one
/// router, one namespace — so the same pair needs one of them renamed. Making the AUTHOR rename it
/// would make the definition target-specific, which is the one thing it exists not to be. So the
/// build renames, and the definitions stay untouched.</para>
///
/// <para><b>A name, once given, never moves.</b> A rename is a migration: it changes a table and an
/// address somebody has bookmarked. So every decision is recorded in <c>cordango.build.json</c> and
/// handed back on the next build, and an assignment that is already in that map is kept whatever the
/// current collisions look like. Without it, adding a third app could rename a table in an app
/// nobody touched — and the failure would arrive as EF dropping a column.</para>
///
/// <para><b>What is renamed, and what is left alone.</b> Only the keys that become one thing in the
/// deployment: an entity (a table, a route, a class), a view, a screen, a screen's address, a
/// workflow, a role. FIELD keys are never touched, because a field is already scoped to its entity —
/// and that distinction is load-bearing rather than tidy. In a real workspace <c>via</c>,
/// <c>groupBy</c>, <c>displayField</c>, <c>who</c>, <c>columns</c> and <c>fields</c> all hold field
/// keys that HAPPEN to equal an entity or role key elsewhere. Rewriting by value would corrupt every
/// one of them, which is why the rewrite is driven by an explicit list of properties
/// (<see cref="EntityRefs"/> and friends) rather than by matching strings.</para>
/// </summary>
public static class WorkspaceLink
{
    /// <summary>The schema's own ceiling for an identifier. A qualified name that would exceed it is
    /// shortened rather than emitted invalid.</summary>
    private const int MaxIdentifier = 63;

    // The kinds. Each is a key that the merged deployment can only have one of.
    private const string Entity = "entity";
    private const string View = "view";
    private const string Page = "page";
    private const string Workflow = "workflow";
    private const string Role = "role";

    // There is deliberately no ROUTE kind. A page has no `route` in the manifest — PageModel derives
    // one from the key ("/" + the key, underscores to hyphens) — so renaming the key renames the
    // address, and keys cannot carry a hyphen, so two different keys can never derive one route.

    /// <summary>
    /// Properties whose value is an ENTITY key, verified against real compiled manifests rather than
    /// guessed from the schema.
    ///
    /// <para><c>whoEntity</c> is the one that proves the point: it is written by the calendar
    /// resolver, appears in no hand-written list anybody would think to make, and a link that missed
    /// it would put a record in nobody's calendar without a word. <c>fromEntity</c> and
    /// <c>toEntity</c> — the two ends of a `relations` entry — were missed by the first version of
    /// this list and found by <c>WorkspaceLinkCoverageTests</c>, which is what that test is for.</para>
    /// </summary>
    private static readonly string[] EntityRefs =
        ["entity", "targetEntity", "parent", "whoEntity", "fromEntity", "toEntity"];

    private static readonly string[] ViewRefs = ["view"];

    /// <summary>Lists that are cumulative: the deployment needs the union of what its apps declare.</summary>
    private static readonly HashSet<string> Concatenated =
        new(StringComparer.Ordinal) { "uses", "plugins" };

    /// <summary>
    /// App-level facts where the first app's answer is simply used, and losing the others costs
    /// nothing a generated application can observe.
    ///
    /// <para><c>presentation</c> is how an app appears in the PLATFORM's launcher, which a standalone
    /// build has none of. <c>archetype</c> and <c>purpose</c> are classification the compiler used on
    /// the way here. <c>theme</c> is a look, and one shell has one. <c>build</c> is how the app was
    /// built, not what it is. Each is listed rather than covered by a rule so that a NEW top-level
    /// property is reported instead of quietly joining them.</para>
    /// </summary>
    private static readonly HashSet<string> FirstWins =
        new(StringComparer.Ordinal) { "presentation", "archetype", "purpose", "theme", "build" };

    /// <summary>What names the APPLICATION rather than what it holds. Replaced by the workspace's own
    /// and never compared between apps — two apps differing about their name is what a workspace
    /// IS.</summary>
    private static readonly HashSet<string> Identity =
        new(StringComparer.Ordinal) { "key", "name", "description", "version", "schemaVersion", "purpose" };

    /// <summary>Merge these apps into one application.</summary>
    /// <param name="identity">The workspace: what the merged application is called.</param>
    /// <param name="apps">Every app of the build, in workspace order, with its compiled manifest.</param>
    /// <param name="previous">Names assigned by an earlier build of this workspace, or null.</param>
    public static LinkedWorkspace Merge(
        WorkspaceIdentity identity,
        IReadOnlyList<(string AppKey, JsonObject Manifest)> apps,
        IReadOnlyDictionary<string, string>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(apps);

        var problems = new List<Diagnostic>();
        var names = Assign(apps, previous, problems);

        // Archetypes cannot be renamed out of a collision: the runtime holds ONE forms descriptor and
        // finds its parts by entity ROLE, not by key. Two apps that both use forms would leave
        // FormsInfo picking whichever came first, silently. Reported rather than linked.
        foreach (var archetype in new[] { "formTemplate", "formResponse" })
        {
            var users = apps
                .Where(a => Array(a.Manifest["entities"]).Any(e => Str(e["role"]) == archetype))
                .Select(a => a.AppKey)
                .ToList();

            if (users.Count > 1)
                problems.Add(new Diagnostic(
                    DiagnosticCodes.WorkspaceKeyCollision,
                    $"{users.Count} apps in this workspace use the forms archetype "
                    + $"({string.Join(", ", users)}), and a merged build carries one form descriptor "
                    + "— its parts are found by entity ROLE, so there is no key to rename and no way "
                    + "to tell the two apart. Keep forms in one app of a workspace for now.",
                    "$.entities"));
        }

        if (problems.Count > 0) return new LinkedWorkspace(new JsonObject(), names, problems);

        // THE BASE IS THE FIRST APP'S WHOLE MANIFEST, not a list of sections this happened to think
        // of. A manifest carries `presentation`, `archetype`, `uses`, `relations`, `plugins`,
        // `purpose`, `custom` and whatever the language gains next, and the first shape of this built
        // the merged document from a fixed list of seven sections — which silently dropped all of
        // them. A workspace of ONE app must come out exactly as it went in, and a whitelist can never
        // promise that; copying and then overriding can.
        var merged = (JsonObject)apps[0].Manifest.DeepClone();

        // The merged application is the WORKSPACE. For a workspace of one these are already its own
        // key and name, so this is a no-op there.
        merged["key"] = identity.Key;
        merged["name"] = identity.Name;

        // Sections that are a LIST of things each app contributes to. Rebuilt from every app, in
        // workspace order, with each row's keys rewritten.
        //
        // `processes` and `commands` carry no key this link decides: both are looked up by
        // (entity, key), so unique entities already make them unique, and renaming a process would
        // change a key the definition's own transitions name. `relations` is here because it NAMES
        // entities even though it declares no key of its own.
        var owns = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["entities"] = Entity,
            ["views"] = View,
            ["pages"] = Page,
            ["workflows"] = Workflow,
            ["roles"] = Role,
            ["processes"] = null,
            ["commands"] = null,
            ["relations"] = null,
        };

        foreach (var (section, kind) in owns)
        {
            if (!apps.Any(a => a.Manifest[section] is JsonArray)) continue;

            var rows = new JsonArray();
            foreach (var (appKey, manifest) in apps)
                foreach (var row in Array(manifest[section]))
                {
                    var copy = (JsonObject)row.DeepClone();

                    // The row's OWN key, whose kind only the section knows. Done here rather than in
                    // the walk, where a nested `key` means a field or a block and must be left alone.
                    if (kind is not null) Swap(copy, "key", kind, appKey, names);

                    Rewrite(copy, appKey, names, apps);
                    rows.Add(copy);
                }

            merged[section] = rows;
        }

        // Lists every app contributes to, concatenated. `uses` and `plugins` are cumulative by
        // nature: a deployment needs every plugin any of its apps enables, and taking the first app's
        // list would turn a second app's feature off with nothing to show for it.
        foreach (var property in Concatenated)
        {
            if (!apps.Any(a => a.Manifest[property] is JsonArray)) continue;

            var all = new JsonArray();
            foreach (var (_, manifest) in apps)
                foreach (var item in manifest[property] as JsonArray ?? [])
                    if (item is not null && !all.Any(x => JsonNode.DeepEquals(x, item)))
                        all.Add(item.DeepClone());

            merged[property] = all;
        }

        // EVERYTHING ELSE the apps carry, and the point of this loop is the ELSE.
        //
        // The first shape of the merge built its document from a list of seven sections, which
        // silently dropped `presentation`, `archetype`, `uses`, `relations`, `build`, `purpose`,
        // `plugins` and `custom`. Only one of those had a visible symptom, two layers down, in a test
        // about something else. So the rule here is inverted: a property this has never been taught
        // about is REPORTED, and the ones it has are listed with the reason they are safe.
        foreach (var (appKey, manifest) in apps.Skip(1))
            foreach (var (property, value) in manifest)
            {
                if (owns.ContainsKey(property) || Identity.Contains(property)) continue;
                if (Concatenated.Contains(property) || FirstWins.Contains(property)) continue;
                if (merged[property] is { } mine && JsonNode.DeepEquals(mine, value)) continue;

                problems.Add(new Diagnostic(
                    DiagnosticCodes.WorkspaceKeyCollision,
                    $"'{appKey}' carries its own '{property}', and a merged build has one — "
                    + $"'{apps[0].AppKey}'s. Nothing here knows how to combine two, so rather than "
                    + $"drop one silently the build stops. Keep '{property}' in one app of this "
                    + "workspace.",
                    $"$.{property}"));
            }

        if (problems.Count > 0) return new LinkedWorkspace(new JsonObject(), names, problems);

        return new LinkedWorkspace(merged, names, []);
    }

    /// <summary>
    /// What every key of every app is called in the merged application.
    ///
    /// <para><b>On a FRESH build</b> a key is left alone unless two apps use it, and then both get
    /// qualified rather than just one: "if two apps use this key, both carry their app's name" is a
    /// rule somebody can state without knowing which app was added first, and the alternative makes
    /// the answer depend on the order of a list in a YAML file.</para>
    ///
    /// <para><b>Against an EXISTING deployment the recorded map wins, and that changes the shape of
    /// the answer.</b> Every name is recorded, including the ones that did not move — and an identity
    /// entry is the load-bearing kind. A workspace that shipped one app has <c>task</c> in its map and
    /// a <c>task</c> table in its database; when a second app arrives with a <c>task</c> of its own,
    /// the first keeps the name it is deployed under and only the newcomer is qualified. So the rule
    /// is symmetric when symmetry is free and asymmetric when it is not, which is the right way round:
    /// nobody's table moves because somebody else added an app.</para>
    /// </summary>
    private static Dictionary<string, string> Assign(
        IReadOnlyList<(string AppKey, JsonObject Manifest)> apps,
        IReadOnlyDictionary<string, string>? previous,
        List<Diagnostic> problems)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (kind, declared) in Declarations(apps))
        {
            var byKey = declared
                .GroupBy(d => d.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(d => d.App).Distinct(StringComparer.Ordinal).Count(),
                    StringComparer.Ordinal);

            foreach (var (app, key) in declared)
            {
                var id = Id(kind, app, key);

                // An earlier build already answered this. Kept whatever the collisions look like now:
                // the whole point of the map is that a name does not move under a deployment.
                if (previous is not null && previous.TryGetValue(id, out var already))
                {
                    names[id] = already;
                    taken.Add($"{kind}:{already}");
                    continue;
                }

                var assigned = byKey[key] > 1 ? Qualify(kind, key, app, problems) : key;

                // A qualified name that lands on something else's name is vanishingly unlikely and
                // silent if it happens, so it is checked rather than assumed.
                if (!taken.Add($"{kind}:{assigned}"))
                {
                    problems.Add(new Diagnostic(
                        DiagnosticCodes.WorkspaceKeyCollision,
                        $"renaming {kind} '{key}' of '{app}' produced '{assigned}', which is already "
                        + "taken in this workspace. Rename one of them in the definition.",
                        "$"));
                }

                names[id] = assigned;
            }
        }

        return names;
    }

    /// <summary>Every key each app declares, by kind.</summary>
    private static IEnumerable<(string Kind, List<(string App, string Key)> Declared)> Declarations(
        IReadOnlyList<(string AppKey, JsonObject Manifest)> apps)
    {
        yield return (Entity, Collect(apps, "entities", e => Str(e["key"])));
        yield return (View, Collect(apps, "views", v => Str(v["key"])));
        yield return (Page, Collect(apps, "pages", p => Str(p["key"])));
        yield return (Workflow, Collect(apps, "workflows", w => Str(w["key"])));
        yield return (Role, Collect(apps, "roles", r => Str(r["key"])));
    }

    private static List<(string App, string Key)> Collect(
        IReadOnlyList<(string AppKey, JsonObject Manifest)> apps, string section, Func<JsonObject, string?> key)
    {
        var found = new List<(string, string)>();
        foreach (var (appKey, manifest) in apps)
            foreach (var row in Array(manifest[section]))
                if (key(row) is { Length: > 0 } k)
                    found.Add((appKey, k));

        return found;
    }

    /// <summary>
    /// The qualified name: the key, then the app that declared it.
    ///
    /// <para>Over the schema's 63-character ceiling the name is shortened and given four characters of
    /// the hash of what it would have been, so it stays unique and stays deterministic — two builds of
    /// the same workspace must produce the same table name.</para>
    /// </summary>
    private static string Qualify(string kind, string key, string app, List<Diagnostic> problems)
    {
        var full = $"{key}_{app}";
        if (full.Length <= MaxIdentifier) return full;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..4].ToLowerInvariant();
        var room = MaxIdentifier - hash.Length - 1;
        var shortened = full[..room] + "_" + hash;

        problems.Add(new Diagnostic(
            DiagnosticCodes.WorkspaceKeyCollision,
            $"{kind} '{key}' of '{app}' collides with another app's, and the qualified name "
            + $"'{full}' is longer than the {MaxIdentifier} characters an identifier may have. It has "
            + $"been shortened to '{shortened}'. Rename one of them to choose the name yourself.",
            "$"));

        return shortened;
    }

    /// <summary>
    /// One row of the merged manifest, with every key it names rewritten.
    ///
    /// <para><b>A row may name ANOTHER app's key</b> — a reference field with <c>targetApp</c>, a block
    /// source with <c>app</c>, a workflow trigger subscribing to a sibling's announcement. Those
    /// resolve against THAT app's names, and the qualifier is then removed: once both apps are in one
    /// deployment the reference is an ordinary local one, which is exactly what makes them able to
    /// work together.</para>
    /// </summary>
    private static void Rewrite(
        JsonNode? node,
        string app,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyList<(string AppKey, JsonObject Manifest)> apps)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var child in array) Rewrite(child, app, names, apps);
                return;

            case JsonObject o:
                // Whose keys does THIS object name? Its own app's, unless it points at a sibling.
                var owner = app;
                foreach (var qualifier in new[] { "targetApp", "app" })
                    if (Str(o[qualifier]) is { } named && apps.Any(a => a.AppKey == named))
                    {
                        owner = named;
                        // Both apps are in one deployment now, so the reference is local. Left in
                        // place for a target that is NOT a sibling — a platform entity still is one.
                        o.Remove(qualifier);
                        break;
                    }

                foreach (var property in EntityRefs) Swap(o, property, Entity, owner, names);
                foreach (var property in ViewRefs) Swap(o, property, View, owner, names);

                // `process.state_entered` announces '<entity>.<state>', so renaming the entity renames
                // the event — the one place in the manifest where a key hides inside a longer string,
                // and a subscription left on the old name waits for something nothing emits.
                //
                // Gated on the EVENT, and that gate is the whole correctness of it. A
                // `command.emitted` name looks identical — 'project.planned' — and is a name the
                // AUTHOR chose in a command's `emits`. Rewriting that because its first word happens
                // to match a renamed entity would break the subscription it was meant to fix.
                if (Str(o["event"]) == "process.state_entered"
                    && Str(o["name"]) is { } announced
                    && announced.Contains('.', StringComparison.Ordinal))
                {
                    var head = announced[..announced.IndexOf('.', StringComparison.Ordinal)];
                    if (names.TryGetValue(Id(Entity, owner, head), out var renamed) && renamed != head)
                        o["name"] = renamed + announced[head.Length..];
                }

                // Children carry the CURRENT app: a nested object's own `app`/`targetApp` is its
                // own business and is read when the walk reaches it.
                foreach (var (_, child) in o.ToList()) Rewrite(child, app, names, apps);

                return;
        }
    }

    /// <summary>Rewrite one property, when it names a key that moved.</summary>
    private static void Swap(
        JsonObject o, string property, string kind, string owner, IReadOnlyDictionary<string, string> names)
    {
        // '*' is every entity, in a permission grant. It is not a key and must survive untouched.
        if (Str(o[property]) is not { Length: > 0 } key || key == "*") return;
        if (names.TryGetValue(Id(kind, owner, key), out var renamed) && renamed != key) o[property] = renamed;
    }

    private static string Id(string kind, string app, string key) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind}:{app}:{key}");

    private static IEnumerable<JsonObject> Array(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonObject>();

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
