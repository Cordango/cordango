// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;
using Cordango.Compile;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// What already exists — in this workspace, on the platform, and in the apps every workspace gets.
///
/// <para><b>The command that stops a concept being modelled twice.</b> An agent asked to link tasks
/// to organizations once declared its own <c>organization</c> entity, because nothing it could run
/// would tell it Organizations already existed; <c>inspect</c> listing the core apps was the fix for
/// that one case. This is the general form: every app's entities, the events it announces, the
/// actions it offers and the rules those actions carry, searchable, so "is there already something
/// for suppliers" has an answer that does not depend on the asker already knowing the app's key.</para>
///
/// <para><b>Search rather than lookup, deliberately.</b> A tenant with a hundred apps cannot hand an
/// agent a list and hope the relevant one is near the top. A query ranks them, through the same
/// <see cref="ContractSearch"/> the platform and the co-creation agent use — three rankings of the
/// same question would be three different answers to it.</para>
///
/// <para><b>Offline first.</b> The workspace's own apps and the core registry need no network and no
/// account, so this works in a repository that has never been logged in. A connection adds the rest
/// of the tenant's apps; not having one is a smaller answer, never an error.</para>
/// </summary>
public static class DiscoverCommand
{
    private const int DefaultLimit = 10;

    public static async Task<int> RunAsync(Args args, Output output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        var section = Section(args);
        var limit = Limit(args);
        var query = args.First;

        var local = Local(selection);
        var core = CoreApps();

        // Remote is additive and optional. `Find` rather than `Resolve`: not being connected is a
        // smaller answer, and refusing to say what IS known because a network is missing would make
        // the offline half worthless.
        var remote = new List<JsonObject>();
        string? instance = null;
        IReadOnlyList<string> unreachable = [];
        if (Connection.Find(args, selection.Workspace) is { } login)
        {
            instance = login.Origin;
            using var connected = new Instance(login.Origin, login.Token);
            var result = await connected.ContractsAsync(query, limit, ct);
            if (result.Ok && result.Body is { } body)
                remote = [.. (body["apps"] as JsonArray ?? [])
                    .Concat(body["coreApps"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(row => row["contract"] as JsonObject)
                    .OfType<JsonObject>()];
            else
                unreachable = result.Errors;
        }

        // The workspace wins over the instance for the same key: the local source is what the author
        // is about to change, and showing them the published copy of an app they have open would
        // describe a version that is already behind.
        var merged = new List<JsonObject>(local);
        var known = new HashSet<string>(local.Select(Key).OfType<string>(), StringComparer.Ordinal);
        var alsoRemote = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in remote)
        {
            var key = Key(contract);
            if (key is not null && !known.Add(key)) { alsoRemote.Add(key); continue; }
            merged.Add(contract);
        }

        List<ContractMatch> matched = args.Value("app") is { Length: > 0 } one
            ? [.. merged.Where(c => Key(c) == one).Select(c => new ContractMatch(c, 0, []))]
            : [.. ContractSearch.Rank(merged, query, limit, section)];

        var payload = new JsonObject
        {
            ["query"] = query,
            ["section"] = section,
            ["instance"] = instance,
            ["unreachable"] = new JsonArray([.. unreachable.Select(e => (JsonNode)e)]),
            ["apps"] = new JsonArray([.. matched.Select(m => (JsonNode)Row(m, alsoRemote, section))]),
            ["coreApps"] = new JsonArray([.. core.Select(c => (JsonNode)c)]),
        };

        return output.Ok(payload, w => Render(w, matched, alsoRemote, section, query, instance, unreachable, core));
    }

    /// <summary>Every readable app in the workspace, as the contract its source compiles to.</summary>
    private static List<JsonObject> Local(Selection selection) =>
        [.. selection.Apps.Select(Pipeline.Check).Select(r => r.Contract).OfType<JsonObject>()];

    /// <summary>
    /// The apps the platform provides to every workspace, in the shape everything else here speaks.
    ///
    /// <para>Static registry data, so it costs no network and no database and is right in a workspace
    /// that has never connected. It is deliberately not a compiled contract: what is known offline is
    /// the systemKey and the entity keys, and inventing the rest would be a contract that lies.</para>
    /// </summary>
    private static List<JsonObject> CoreApps() =>
        [.. CoreAppRegistry.All.Select(c => new JsonObject
        {
            ["app"] = c.SystemKey,
            ["name"] = c.Name,
            ["entities"] = new JsonArray([.. c.Entities.Select(e => (JsonNode)e.Key)]),
        })];

    private static JsonObject Row(ContractMatch match, HashSet<string> alsoRemote, string? section)
    {
        var contract = match.Contract;
        var key = Key(contract);
        var row = new JsonObject
        {
            ["app"] = key,
            ["name"] = (contract["identity"] as JsonObject)?["name"]?.DeepClone(),
            ["purpose"] = (contract["purpose"] as JsonObject)?["summary"]?.DeepClone(),
            ["alsoRemote"] = key is not null && alsoRemote.Contains(key),
            // What hit, so a result never has to be taken on trust — and so an agent can go straight
            // to the event it was looking for instead of re-reading the whole list.
            ["matched"] = new JsonArray([.. match.Matched.Select(m => (JsonNode)m)]),
        };

        if (section is null or "all" or "entities") row["entities"] = Names(contract, "entities", "key");
        if (section is null or "all" or "events") row["events"] = Names(contract, "events", "name");
        if (section is null or "all" or "actions") row["actions"] = Names(contract, "actions", "id");
        if (section is null or "all" or "rules") row["rules"] = Names(contract, "rules", "id");
        row["uses"] = new JsonArray([.. (contract["dependencies"] as JsonArray ?? [])
            .OfType<JsonObject>().Select(d => d["app"]?.DeepClone()).OfType<JsonNode>()]);
        return row;
    }

    private static void Render(TextWriter w, List<ContractMatch> apps, HashSet<string> alsoRemote,
        string? section, string? query, string? instance, IReadOnlyList<string> unreachable,
        List<JsonObject> core)
    {
        if (apps.Count == 0)
        {
            w.WriteLine(query is { Length: > 0 }
                ? $"nothing matching '{query}'."
                : "no apps to describe yet.");
        }

        foreach (var match in apps)
        {
            var contract = match.Contract;
            var key = Key(contract);
            w.WriteLine($"{key}{(key is not null && alsoRemote.Contains(key) ? "   (also published)" : "")}");
            if ((contract["purpose"] as JsonObject)?["summary"]?.GetValue<string>() is { Length: > 0 } purpose)
                w.WriteLine($"  {purpose}");

            // A search answers with what it FOUND. Printing every event of a matching app buries the
            // one the question was about in forty that were not, which is how a list stops being an
            // answer; the full picture is one `--app <key>` away.
            if (match.Matched.Count > 0)
            {
                w.WriteLine($"  matched   {string.Join(", ", match.Matched)}");
                w.WriteLine();
                continue;
            }

            Section(w, "entities", contract, "entities", "key", section);
            Section(w, "announces", contract, "events", "name", section);
            Section(w, "actions", contract, "actions", "id", section);
            Section(w, "rules", contract, "rules", "id", section);

            var uses = (contract["dependencies"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(d => d["app"]?.GetValue<string>()).OfType<string>().ToList();
            if (uses.Count > 0) w.WriteLine($"  uses      {string.Join(", ", uses)}");
            w.WriteLine();
        }

        w.WriteLine("Core apps — the platform provides these to every workspace. LINK to them,");
        w.WriteLine("never re-declare them: type: reference, targetApp: <key>, target: <entity>.");
        foreach (var c in core)
            w.WriteLine($"  {c["app"]!.GetValue<string>(),-24} "
                + string.Join(", ", (c["entities"] as JsonArray ?? []).Select(e => e!.GetValue<string>())));

        if (instance is null)
            w.WriteLine("\nnot connected — showing this workspace only. `cordango login` adds the rest.");
        else if (unreachable.Count > 0)
            w.WriteLine($"\n{instance} could not be reached: {string.Join("; ", unreachable)}");
    }

    private static void Section(TextWriter w, string caption, JsonObject contract,
        string key, string field, string? section)
    {
        if (section is not (null or "all") && section != key) return;
        var names = (contract[key] as JsonArray ?? []).OfType<JsonObject>()
            .Select(x => x[field]?.GetValue<string>()).OfType<string>().ToList();
        if (names.Count == 0) return;
        w.WriteLine($"  {caption,-9} {string.Join(", ", names)}");
    }

    private static JsonArray Names(JsonObject contract, string key, string field) =>
        new([.. (contract[key] as JsonArray ?? []).OfType<JsonObject>()
            .Select(x => x[field]?.DeepClone()).OfType<JsonNode>()]);

    private static string? Key(JsonObject contract) =>
        (contract["identity"] as JsonObject)?["key"]?.GetValue<string>();

    private static string? Section(Args args) =>
        args.Has("entities") ? "entities"
        : args.Has("events") ? "events"
        : args.Has("actions") ? "actions"
        : args.Has("rules") ? "rules"
        : null;

    private static int Limit(Args args) =>
        int.TryParse(args.Value("limit"), out var n) && n > 0 ? Math.Min(n, 100) : DefaultLimit;
}
