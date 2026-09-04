// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compile;

/// <summary>
/// The one place a contract becomes bytes.
///
/// <para><b>Because "the same contract everywhere" is a claim about BYTES.</b> Two hosts producing
/// the same <see cref="JsonObject"/> and writing it with their own serializer settings produce two
/// different files — the CLI already appended a trailing newline where the runtime store did not,
/// which is enough to break a hash comparison and every promise built on one. Indentation, escaping,
/// the trailing newline and the hash are decided here and nowhere else.</para>
///
/// <para><b>The hash covers the contract without its own hash</b>, which is the only way a document
/// can carry it. Sealing twice is idempotent: the second pass removes the first's value before
/// hashing, so a re-sealed contract keeps the hash it had.</para>
/// </summary>
public static class ContractWriter
{
    /// <summary>
    /// Every setting that decides a byte, stated rather than defaulted.
    ///
    /// <para><c>NewLine</c> is the one that matters most and is the least obvious: it defaults to the
    /// PLATFORM's newline, so a contract written on Windows and the same contract written in a Linux
    /// container would differ on every line while saying exactly the same thing. The encoder is
    /// pinned for the same reason a sibling artifact pins it — a German label should be the German
    /// label, not sixty escape sequences — and matching the definition and manifest writers means the
    /// three files in a build directory read alike.</para>
    /// </summary>
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The contract with <c>identity.contractHash</c> filled in. The input is not
    /// modified.</summary>
    public static JsonObject Seal(JsonObject contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var sealedCopy = (JsonObject)contract.DeepClone();
        var identity = sealedCopy["identity"] as JsonObject;
        identity?.Remove("contractHash");

        // Ordinal-canonical, so the hash does not depend on the order this document's keys happen to
        // be built in — only on what it says.
        var hash = DefinitionHash.Of(sealedCopy);
        if (identity is not null) identity["contractHash"] = hash;
        return sealedCopy;
    }

    /// <summary>The exact bytes of <c>contract.json</c>, sealed. Every host writes these and nothing
    /// else.</summary>
    public static byte[] Bytes(JsonObject contract) =>
        Encoding.UTF8.GetBytes(Text(contract));

    /// <summary>The same, as text — for callers that have a string-shaped sink.</summary>
    public static string Text(JsonObject contract) =>
        Seal(contract).ToJsonString(Pretty) + "\n";

    /// <summary>The hash a sealed contract carries, or null when it has none.</summary>
    public static string? HashOf(JsonObject? contract) =>
        (contract?["identity"] as JsonObject)?["contractHash"]?.GetValue<string>();

    /// <summary>The definition hash a contract was built from, or null for a provisional one.</summary>
    public static string? DefinitionHashOf(JsonObject? contract) =>
        (contract?["identity"] as JsonObject)?["definitionHash"] is JsonValue v
        && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Parse a contract file. Null when it is not one — a truncated or hand-mangled file is
    /// treated as absent rather than served as a contract.</summary>
    public static JsonObject? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text) is JsonObject o && o["kind"]?.GetValue<string>() == AppContract.Kind
                ? o
                : null;
        }
        catch (JsonException) { return null; }
    }
}
