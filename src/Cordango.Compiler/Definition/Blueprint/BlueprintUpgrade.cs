// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// Reads a blueprint written against an older schema version.
///
/// <para>This exists BEFORE the first user blueprint is persisted, on purpose. A stored document
/// outlives the code that wrote it, and the moment one exists in a database the choice is no longer
/// "should there be an upgrade path" but "how do we recover the ones we cannot read". Adding the
/// step first costs almost nothing; adding it later costs data.</para>
///
/// <para>Each step takes the raw document from one version to the next and is never allowed to
/// consult anything outside it — an upgrade that needed the rest of the world could not be replayed
/// over a backup.</para>
/// </summary>
public static class BlueprintUpgrade
{
    /// <summary>One version step. Ordered; every stored version must have a path to current.</summary>
    private static readonly (string From, string To, Action<JsonObject> Apply)[] Steps =
    [
        ("1.0", "1.1", doc =>
        {
            doc["reviews"] ??= new JsonArray();
            doc["changeRequests"] ??= new JsonArray();
            doc["generation"] ??= new JsonArray();
        }),
    ];

    /// <summary>Whether a version is one this build knows how to read at all.</summary>
    public static bool CanRead(string? schemaVersion) =>
        schemaVersion == BlueprintSchemaVersion.Current
        || Steps.Any(s => s.From == schemaVersion);

    /// <summary>
    /// Brings a raw blueprint document up to <see cref="BlueprintSchemaVersion.Current"/>.
    ///
    /// <para>Returns the document unchanged when it is already current. Throws when the version is
    /// unknown — which happens when a NEWER build wrote it, and reading that document with older
    /// rules would silently misinterpret whatever changed. Refusing is the safe answer.</para>
    /// </summary>
    public static JsonObject ToCurrent(JsonObject doc)
    {
        var version = doc["schemaVersion"]?.GetValue<string>();
        if (version == BlueprintSchemaVersion.Current) return doc;

        if (!CanRead(version))
            throw new InvalidOperationException(
                $"blueprint schema version '{version}' is not one this build can read "
                + $"(current is {BlueprintSchemaVersion.Current}). A document from a NEWER build must "
                + "not be read with older rules.");

        var working = (JsonObject)doc.DeepClone();
        var guard = Steps.Length + 1;
        while (working["schemaVersion"]?.GetValue<string>() is { } current
               && current != BlueprintSchemaVersion.Current)
        {
            if (guard-- <= 0)
                throw new InvalidOperationException("blueprint upgrade steps do not converge — check for a cycle");
            var step = Steps.FirstOrDefault(s => s.From == current);
            if (step.Apply is null)
                throw new InvalidOperationException($"no upgrade step from blueprint schema '{current}'");
            step.Apply(working);
            working["schemaVersion"] = step.To;
        }
        return working;
    }

    /// <summary>Parse tolerant of an older stored version — the entry point every store should use
    /// rather than <see cref="BlueprintJson.Parse"/>, which assumes current.</summary>
    public static Blueprint Read(string json)
    {
        var doc = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("blueprint document is not an object");
        var upgraded = doc["schemaVersion"]?.GetValue<string>() != BlueprintSchemaVersion.Current;
        var frozenHash = doc["contentHash"]?.GetValue<string>();
        if (upgraded && frozenHash is not null && !StringComparer.Ordinal.Equals(frozenHash, RawContentHash(doc)))
            throw new InvalidOperationException("this legacy blueprint was edited after it was approved");

        // Review state is NOT initialised here. ReviewFlow.Reconcile owns that, and it runs in
        // BlueprintStore on every read and write — doing it in both places would give two answers
        // to "which reviews are stale" and no way to tell which one a caller got.
        var blueprint = BlueprintJson.Parse(ToCurrent(doc).ToJsonString());

        // A legacy approval hash proves the original stored document, not the upgraded projection:
        // 1.1 necessarily adds review metadata and changes the bytes. Only after the old bytes have
        // been verified above may the deterministic in-memory upgrade receive its current hash.
        return upgraded && frozenHash is not null
            ? blueprint with { ContentHash = BlueprintJson.ContentHash(blueprint) }
            : blueprint;
    }

    private static string RawContentHash(JsonObject doc)
    {
        var unhashed = (JsonObject)doc.DeepClone();
        unhashed.Remove("contentHash");
        var bytes = System.Text.Encoding.UTF8.GetBytes(unhashed.ToJsonString(BlueprintJson.Options));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
