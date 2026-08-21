// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>One shipped version of a core app's definition.</summary>
/// <param name="Version">The definition's own <c>version</c> field, e.g. <c>1.0.0</c>.</param>
/// <param name="Json">The definition document as authored.</param>
public sealed record CoreAppVersion(string Version, string Json)
{
    public JsonNode Node() => JsonNode.Parse(Json)
        ?? throw new InvalidOperationException($"core definition '{Version}' is not valid JSON");
}

/// <summary>One entity inside a core app — what a cross-app reference is allowed to target.</summary>
/// <param name="Key">The <c>targetEntity</c> value. Not the label: Organizations declares
/// <c>organization</c> and labels it "Company", and an author who sees only the label guesses wrong.</param>
/// <param name="Description">The entity's own description, which is where a core app explains why it is
/// the canonical record rather than something an app should declare for itself.</param>
public sealed record CoreEntity(
    string Key,
    string Label,
    string? Description,
    IReadOnlyList<string> FieldKeys);

/// <summary>A core app as declared in <c>schema/core/registry.json</c>.</summary>
/// <param name="SystemKey">Permanent logical identity, e.g. <c>core_organizations</c>. This is what
/// an app definition puts in <c>targetApp</c>; it is never the handle.</param>
/// <param name="DefaultAccess"><c>AllMembersRead</c> or <c>AdminOnly</c>.</param>
/// <param name="DefaultRole">The definition role key every tenant member implicitly holds under
/// <c>AllMembersRead</c>.</param>
/// <param name="Versions">Every shipped version, oldest first. All of them are kept so the
/// compatibility validator can replay the real upgrade path.</param>
public sealed record CoreApp(
    string SystemKey,
    string Name,
    string DefaultAccess,
    string DefaultRole,
    IReadOnlyList<CoreAppVersion> Versions)
{
    public const string AccessAllMembersRead = "AllMembersRead";
    public const string AccessAdminOnly = "AdminOnly";

    /// <summary>The version that gets provisioned — the last one shipped.</summary>
    public CoreAppVersion Current => Versions[^1];

    /// <summary>Every tenant member reaches this app without a grant row.</summary>
    public bool AllMembersRead => DefaultAccess == AccessAllMembersRead;

    /// <summary>
    /// The entities the current definition declares, in authored order.
    ///
    /// <para><b>Described, not just named, because naming them was not enough.</b> The gate has always
    /// been able to check a reference into a core app against this list — but nothing an author could
    /// run ever printed it, so an agent asked to link tasks to organizations declared its own
    /// <c>organization</c> entity instead. It was the correct inference from what it could see. This is
    /// what <c>cord inspect</c> and <c>cord vocabulary</c> read so that stops being true.</para>
    /// </summary>
    public IReadOnlyList<CoreEntity> Entities => _entities ??= LoadEntities();
    private IReadOnlyList<CoreEntity>? _entities;

    /// <summary>The entity keys the current definition declares — what a cross-app reference to this
    /// core app is allowed to target. Static, so <see cref="Gate"/> can validate a reference without
    /// touching a database.</summary>
    public IReadOnlySet<string> EntityKeys =>
        _entityKeys ??= new HashSet<string>(Entities.Select(e => e.Key), StringComparer.Ordinal);
    private IReadOnlySet<string>? _entityKeys;

    private IReadOnlyList<CoreEntity> LoadEntities()
    {
        var list = new List<CoreEntity>();
        if (Current.Node()["entities"] is not JsonArray entities) return list;

        foreach (var e in entities)
        {
            if (e?["key"]?.GetValue<string>() is not { } key) continue;
            var fields = new List<string>();
            if (e["fields"] is JsonArray fs)
                foreach (var f in fs)
                    if (f?["key"]?.GetValue<string>() is { } fk) fields.Add(fk);

            list.Add(new CoreEntity(
                key,
                e["label"]?.GetValue<string>() ?? key,
                e["description"]?.GetValue<string>(),
                fields));
        }
        return list;
    }
}

/// <summary>
/// The platform's core apps, read once from the embedded <c>schema/core/registry.json</c>.
///
/// Deliberately STATIC data with no dependencies: <see cref="Gate"/> validates cross-app references
/// against it and must stay a pure single-document function — a gate that reached for a database
/// would make validity depend on which environment happened to run it. Callers that provision or
/// serve core apps take the registration list as a parameter, so tests can drive them with their own.
/// </summary>
public static class CoreAppRegistry
{
    public static readonly IReadOnlyList<CoreApp> All = Load();

    private static readonly Dictionary<string, CoreApp> ByKey =
        All.ToDictionary(a => a.SystemKey, StringComparer.Ordinal);

    /// <summary>The core app with this system key, or null. Used by the gate to decide whether a
    /// <c>targetApp</c> is a known core app or an arbitrary (still unvalidated) cross-app key.</summary>
    public static CoreApp? Find(string? systemKey) =>
        systemKey is not null && ByKey.TryGetValue(systemKey, out var a) ? a : null;

    public static bool IsCoreKey(string? systemKey) => Find(systemKey) is not null;

    private static IReadOnlyList<CoreApp> Load()
    {
        var doc = JsonNode.Parse(Schemas.LoadResource("core/registry.json"))
            ?? throw new InvalidOperationException("core/registry.json is not valid JSON");
        var apps = new List<CoreApp>();
        foreach (var node in doc["apps"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject a) continue;
            var key = Str(a, "systemKey") ?? throw new InvalidOperationException("core app entry has no systemKey");
            var versions = new List<CoreAppVersion>();
            foreach (var v in a["versions"] as JsonArray ?? new JsonArray())
            {
                if (v?.GetValue<string>() is not { Length: > 0 } label)
                    throw new InvalidOperationException($"core app '{key}' has a non-string version entry");
                var json = Schemas.LoadResource($"core/{key}.{label}.json");
                var declared = JsonNode.Parse(json)?["version"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"core definition '{key}.{label}' declares no version");
                versions.Add(new CoreAppVersion(declared, json));
            }
            if (versions.Count == 0)
                throw new InvalidOperationException($"core app '{key}' lists no versions");
            apps.Add(new CoreApp(
                key,
                Str(a, "name") ?? key,
                Str(a, "defaultAccess") ?? CoreApp.AccessAdminOnly,
                Str(a, "defaultRole") ?? "viewer",
                versions));
        }
        return apps;
    }

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();
}
