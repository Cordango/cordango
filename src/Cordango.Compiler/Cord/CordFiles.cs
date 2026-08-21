// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cord;

/// <summary>One materialized <c>.cord</c> file.</summary>
/// <param name="Path">Repo-relative with forward slashes: <c>app.cord</c>,
/// <c>entities/&lt;key&gt;.cord</c>, <c>workflows/lifecycles|actions|automations/&lt;key&gt;.cord</c>,
/// <c>roles/&lt;key&gt;.cord</c>, <c>views/&lt;key&gt;.cord</c>.</param>
/// <param name="Content">Canonical bytes: 2-space indent, writer key order, LF line endings, one
/// trailing newline. Canonical formatting is part of the contract, not a nicety — two paths that
/// produce the same application must produce the same bytes, or every diff is whitespace noise and
/// "the files and the database agree" stops being checkable.</param>
/// <param name="ContentHash">Hex SHA-256 of <paramref name="Content"/>.</param>
public sealed record CordFileEntry(string Path, string Content, string ContentHash);

/// <summary>What one materialization produced.</summary>
/// <param name="Unwritable">Pointers into the model naming what no operation can express, verbatim
/// from <see cref="CordDocument.Write"/>. Empty for every Cord-authored app by construction — the
/// authoring surface has no raw escape hatch — and non-empty exactly when an IMPORTED app carries
/// overlay the UI slice has not modelled yet. Reported loudly rather than dropped: files claiming to
/// be source while silently missing screens would be the leak plan risk 3 forbids.</param>
public sealed record CordMaterialized(IReadOnlyList<CordFileEntry> Files, IReadOnlyList<string> Unwritable)
{
    public bool Complete => Unwritable.Count == 0;
}

/// <summary>
/// The G3 splitter: a <see cref="CordApp"/> as per-aggregate <c>.cord</c> files, and the way back.
///
/// <para><b>A partition, not a second serializer.</b> <see cref="CordDocument.Write"/> already emits
/// the app as the operations that would create it, and every operation names exactly one aggregate —
/// so a file is simply the ops that share a <see cref="CordAggregates.Target"/>, wrapped in
/// <c>{"ops":[…]}</c>. The reader is <c>CordTransaction.Prepare</c>, which already exists; there is
/// no new format and no new parser, which is the whole reason file format v1 is JSON ops.</para>
///
/// <para><b><c>app.cord</c> carries identity AND order.</b> Array order is meaningful in an App
/// Definition (entity order drives navigation, page order drives the shell) and
/// <c>DefinitionHash</c> hashes it — but a set of files has no order of its own, and the GitHub tree
/// these rows will one day sync to has none either. So the identity file records the materialization
/// order explicitly, and <see cref="Join"/> reassembles by it. Files it does not list (a future pull
/// adding a new entity) are appended in path order rather than lost.</para>
/// </summary>
public static class CordFiles
{
    public const string AppFile = "app.cord";

    /// <summary>Serializer for canonical file bytes. LF regardless of platform, 2-space indent,
    /// relaxed escaping so a German label is a German label rather than <c>ü</c>.</summary>
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The app as canonical per-aggregate files, in materialization order
    /// (<see cref="AppFile"/> first).</summary>
    public static CordMaterialized Materialize(CordApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var written = CordDocument.Write(app);

        // Partition the written ops by the aggregate each one names, preserving op order within a
        // file and first-seen order across files — both are the model's own order, which is what
        // Join has to reproduce for the round trip to be exact.
        var order = new List<string>();
        var grouped = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var op in written.Json["ops"]!.AsArray().OfType<JsonObject>())
        {
            var path = PathOf(op);
            if (!grouped.TryGetValue(path, out var ops))
            {
                ops = [];
                grouped[path] = ops;
                order.Add(path);
            }
            ops.Add(op.DeepClone());
        }

        var identity = new JsonObject { ["key"] = app.Key, ["name"] = app.Name, ["version"] = app.Version };
        // Description is identity, not an operation — CordDocument's ops cannot say it, so the
        // identity file carries it or the round trip silently loses what the user wrote.
        if (app.Description is { Length: > 0 } description) identity["description"] = description;
        identity["order"] = new JsonArray([.. order.Select(p => (JsonNode)p!)]);

        var files = new List<CordFileEntry> { Entry(AppFile, identity) };
        files.AddRange(order.Select(path => Entry(path, new JsonObject { ["ops"] = grouped[path] })));

        return new CordMaterialized(files, written.Unwritable);
    }

    /// <summary>
    /// Files back to one ops document <c>{key, name, version, description?, ops[]}</c> — the shape
    /// <c>CordTransaction.Prepare</c> replays and the future Git-pull path parses.
    /// </summary>
    /// <returns>The document, plus problems for anything unreadable. A problem never throws — a
    /// sync path needs "this file is wrong" per file, not one exception for the tree.</returns>
    public static (JsonObject Doc, IReadOnlyList<string> Problems) Join(IEnumerable<CordFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var problems = new List<string>();
        var byPath = new Dictionary<string, CordFileEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!byPath.TryAdd(entry.Path, entry))
                problems.Add($"{entry.Path}: appears more than once");
        }

        var doc = new JsonObject();
        var ordered = new List<string>();

        if (byPath.Remove(AppFile, out var appFile))
        {
            if (Parse(appFile, problems) is { } identity)
            {
                doc["key"] = identity["key"]?.DeepClone();
                doc["name"] = identity["name"]?.DeepClone();
                doc["version"] = identity["version"]?.DeepClone();
                if (identity["description"] is { } d) doc["description"] = d.DeepClone();
                foreach (var p in (identity["order"] as JsonArray ?? []).OfType<JsonValue>())
                    if (p.TryGetValue<string>(out var path)) ordered.Add(path);
            }
        }
        else
        {
            problems.Add($"{AppFile}: missing — the identity and file order live there");
        }

        // Listed files in the recorded order, then anything the manifest does not know in path
        // order — appended rather than dropped, because a pulled tree may legitimately carry a file
        // this app.cord has never heard of.
        var ops = new JsonArray();
        foreach (var path in ordered)
        {
            if (!byPath.Remove(path, out var entry))
            {
                problems.Add($"{path}: listed in {AppFile} but not present");
                continue;
            }
            Append(entry);
        }
        foreach (var entry in byPath.Values.OrderBy(e => e.Path, StringComparer.Ordinal))
            Append(entry);

        doc["ops"] = ops;
        return (doc, problems);

        void Append(CordFileEntry entry)
        {
            // The bytes are what they claim to be. Cheap, and the only check that can catch a file
            // store that has drifted from the hashes recorded against it.
            if (DefinitionHash.OfText(entry.Content) != entry.ContentHash)
            {
                problems.Add($"{entry.Path}: content does not match its recorded hash");
                return;
            }

            if (Parse(entry, problems) is not { } file) return;
            if (file["ops"] is not JsonArray fileOps)
            {
                problems.Add($"{entry.Path}: no `ops` array");
                return;
            }

            for (var i = 0; i < fileOps.Count; i++)
            {
                if (fileOps[i] is not JsonObject op)
                {
                    problems.Add($"{entry.Path}: op {i} is not an object");
                    continue;
                }

                // EVERY OPERATION MUST BELONG IN THE FILE THAT CARRIES IT.
                //
                // A hash proves the bytes are unchanged; it says nothing about what is inside them,
                // and "inside them" is exactly what a hand edit or a pulled commit gets wrong. Without
                // this, `views/hiring.cord` could carry {"op":"remove","entity":"scenario"} and the
                // replay would honour it — one screen's file quietly deleting the domain, with the
                // per-aggregate layout that makes review tractable providing the cover.
                //
                // Refused per operation rather than per file: naming the one that is misplaced is what
                // lets somebody fix it, and the rest of the file may be perfectly good.
                string? belongs;
                try
                {
                    belongs = PathOf(op);
                }
                catch (InvalidOperationException)
                {
                    problems.Add($"{entry.Path}: op {i} is '{(string?)op["op"]}', which is not an operation");
                    continue;
                }

                if (!string.Equals(belongs, entry.Path, StringComparison.Ordinal))
                {
                    problems.Add($"{entry.Path}: op {i} belongs in {belongs} — an operation is only "
                        + "read from the file for its own aggregate");
                    continue;
                }

                ops.Add(op.DeepClone());
            }
        }
    }

    /// <summary>Where one written operation files. Kind subdirectories under <c>workflows/</c>
    /// keep a lifecycle (keyed by its entity) from colliding with an action or automation that
    /// happens to share the key — the layout the Budget Planner example source already uses.
    ///
    /// <para>Public because it is the file layout itself: <see cref="Materialize"/> partitions by it,
    /// <see cref="Join"/> re-checks against it, and a sync host has to answer "which file does this
    /// change touch" without reimplementing the mapping and drifting from it.</para></summary>
    /// <exception cref="InvalidOperationException">The operation name is not one Cord has. Callers
    /// reading content they did not write — a pull, a hand edit — catch this rather than trusting it.
    /// </exception>
    public static string PathOf(JsonObject op) => (string?)op["op"] switch
    {
        "upsert_entity" => $"entities/{Str(op["entity"], "key")}.cord",
        "upsert_field" or "remove" => $"entities/{(string?)op["entity"]}.cord",
        "upsert_lifecycle" => $"workflows/lifecycles/{Str(op["lifecycle"], "entity")}.cord",
        "upsert_action" => $"workflows/actions/{Str(op["action"], "key")}.cord",
        "upsert_automation" => $"workflows/automations/{Str(op["automation"], "key")}.cord",
        "upsert_role" => $"roles/{Str(op["role"], "key")}.cord",
        "remove_behaviour" => (string?)op["kind"] == CordBehaviourKinds.Role
            ? $"roles/{(string?)op["key"]}.cord"
            : $"workflows/{(string?)op["kind"]}s/{(string?)op["key"]}.cord",
        "upsert_screen" => $"views/{Str(op["screen"], "key")}.cord",
        "remove_screen" => $"views/{(string?)op["key"]}.cord",
        "upsert_screen_tab" or "remove_screen_tab" => $"views/{(string?)op["screen"]}.cord",
        var name => throw new InvalidOperationException($"no file mapping for operation '{name}'"),
    };

    private static string? Str(JsonNode? node, string key) =>
        (node as JsonObject)?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static CordFileEntry Entry(string path, JsonObject content) =>
        FromContent(path, content.ToJsonString(Canonical) + "\n");

    /// <summary>An entry from raw content — the hash is always derived here, so a caller cannot
    /// construct one whose hash and content disagree.</summary>
    public static CordFileEntry FromContent(string path, string content) =>
        new(path, content, DefinitionHash.OfText(content));

    private static JsonObject? Parse(CordFileEntry entry, List<string> problems)
    {
        try
        {
            if (JsonNode.Parse(entry.Content) is JsonObject o) return o;
            problems.Add($"{entry.Path}: not a JSON object");
        }
        catch (JsonException ex)
        {
            problems.Add($"{entry.Path}: unreadable ({ex.Message})");
        }
        return null;
    }
}
