// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

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

/// <summary>
/// A core app, as its published CONTRACT describes it.
///
/// <para><b>This is what the app OFFERS, not how it is built.</b> The full definitions — field types,
/// options, roles, pages, processes, workflows — are not in this repository, and their absence is
/// deliberate rather than an oversight. <see cref="Cordango.Compile.AppCompiler"/> and the standalone
/// generator are both here and both Apache-2.0, so a published definition is not a description of an
/// application, it is a buildable copy of one. A contract carries no field types, so it cannot be
/// compiled: the boundary enforces itself rather than relying on a rule somebody has to remember.</para>
///
/// <para>What is here is exactly what an author or an agent needs in order to REFERENCE a core app
/// instead of re-modelling it — which is the whole reason the gate knows about core apps at all.</para>
/// </summary>
/// <param name="SystemKey">Permanent logical identity, e.g. <c>core_organizations</c>. This is what
/// an app definition puts in <c>targetApp</c>; it is never the handle.</param>
public sealed record CoreApp(string SystemKey, string Name, IReadOnlyList<CoreEntity> Entities)
{
    /// <summary>The entity keys the contract declares — what a cross-app reference to this core app
    /// is allowed to target. Static, so <see cref="Gate"/> can validate a reference without touching
    /// a database.</summary>
    public IReadOnlySet<string> EntityKeys =>
        _entityKeys ??= new HashSet<string>(Entities.Select(e => e.Key), StringComparer.Ordinal);
    private IReadOnlySet<string>? _entityKeys;
}

/// <summary>
/// The platform's core apps, read once from the embedded <c>schema/core/registry.json</c> and the
/// contract beside each entry.
///
/// Deliberately STATIC data with no dependencies: <see cref="Gate"/> validates cross-app references
/// against it and must stay a pure single-document function — a gate that reached for a database
/// would make validity depend on which environment happened to run it. Callers that serve core apps
/// take the registration list as a parameter, so tests can drive them with their own.
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
            var key = Str(a, "systemKey")
                ?? throw new InvalidOperationException("core app entry has no systemKey");

            var contract = JsonNode.Parse(Schemas.LoadResource($"core/{key}.contract.json"))
                ?? throw new InvalidOperationException($"core contract '{key}' is not valid JSON");

            apps.Add(new CoreApp(
                key,
                Str(a, "name") ?? Str(contract["identity"] as JsonObject, "name") ?? key,
                Entities(contract)));
        }
        return apps;
    }

    /// <summary>The contract's entities, flattened to what a reference needs: the key it may target,
    /// a label to print, why the entity exists, and the field names it holds.</summary>
    private static IReadOnlyList<CoreEntity> Entities(JsonNode contract)
    {
        var list = new List<CoreEntity>();
        foreach (var e in contract["entities"] as JsonArray ?? new JsonArray())
        {
            if (e is not JsonObject entity) continue;
            if (Str(entity, "key") is not { } key) continue;

            var fields = new List<string>();
            foreach (var f in entity["fields"] as JsonArray ?? new JsonArray())
                if (f?["key"]?.GetValue<string>() is { } fk) fields.Add(fk);

            list.Add(new CoreEntity(key, Str(entity, "label") ?? key, Str(entity, "description"), fields));
        }
        return list;
    }

    private static string? Str(JsonObject? o, string key) => o?[key]?.GetValue<string>();
}
