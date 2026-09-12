// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// The application's own functions, as the expression checker asks about them.
///
/// <para><b>One reader, because there are three askers.</b> The gate validates the lowered
/// document, <c>CordCheck</c> validates the semantic model, and the emitter translates — and all
/// three have to agree about what <c>custom.discount</c> takes and answers, or an expression passes
/// one and fails another. <c>ComputedCodeAgreementTests</c> exists because that has happened before
/// with select codes.</para>
///
/// <para>A missing or malformed entry answers null rather than throwing. The section is validated
/// on its own elsewhere; here, an unreadable declaration means the call is simply not declared,
/// which is the message an author can act on.</para>
/// </summary>
public static class CustomSignatures
{
    /// <summary>An empty lookup, for a caller with no custom section and nothing to say about it.</summary>
    public static readonly Func<string, CustomSignature?> None = _ => null;

    public static Func<string, CustomSignature?> From(JsonNode? custom)
    {
        if (custom is not JsonObject section || section["functions"] is not JsonArray declared)
            return None;

        var byName = new Dictionary<string, CustomSignature>(StringComparer.Ordinal);

        foreach (var node in declared)
        {
            if (node is not JsonObject fn) continue;
            if ((string?)fn["name"] is not { Length: > 0 } name) continue;
            if (Kind((string?)fn["returns"]) is not { } returns) continue;

            var parameters = new List<ComputedValueKind>();
            var ok = true;

            foreach (var p in fn["params"] as JsonArray ?? [])
            {
                if (p is JsonObject param && Kind((string?)param["kind"]) is { } kind)
                {
                    parameters.Add(kind);
                    continue;
                }

                ok = false;
                break;
            }

            // Last declaration wins rather than first, matching how the rest of the compiler reads
            // a repeated key. A duplicate is refused where the section is validated, so this only
            // decides what an already-refused document looks like on the way to that message.
            if (ok) byName[name] = new CustomSignature(parameters, returns);
        }

        return name => byName.GetValueOrDefault(name);
    }

    private static ComputedValueKind? Kind(string? written) => written switch
    {
        "number" => ComputedValueKind.Number,
        "boolean" => ComputedValueKind.Boolean,
        "text" => ComputedValueKind.Text,
        _ => null,
    };
}
