// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// The content hash of an App Definition, over a key-ORDER-INDEPENDENT rendering.
///
/// <para>Definitions round-trip through a <c>jsonb</c> column, and Postgres stores jsonb as a parsed
/// structure: it reorders keys and drops whitespace, so the text that comes back is never the text
/// that went in. Hashing raw text would report "changed" on every read — which is why
/// <c>CoreAppProvisioner</c> grew this first, to stop rebuilding every tenant on every boot.</para>
///
/// <para>It matters twice more now. A <c>submit_candidate</c> hash must survive being persisted and
/// re-read before <c>finish</c> quotes it back, and an approval binds a definition hash that has to
/// still match when the app is published. All three want the same function, so it lives here rather
/// than once per caller — two definitions of "the same definition" is exactly the kind of divergence
/// that makes an approval mean nothing.</para>
/// </summary>
public static class DefinitionHash
{
    /// <summary>Hex SHA-256 of the canonical rendering of <paramref name="node"/>.</summary>
    public static string Of(JsonNode? node) => OfText(Canonical(node)?.ToJsonString() ?? "null");

    public static string OfText(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>The same document with every object's keys in ordinal order.</summary>
    public static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Aggregate(new JsonObject(), (acc, p) => { acc[p.Key] = Canonical(p.Value?.DeepClone()); return acc; }),
        // Array order is meaningful (field order drives column order and rendering), so it stands.
        JsonArray a => new JsonArray(a.Select(x => Canonical(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}
