// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.SourceGen;

/// <summary>
/// Everything the language allows, read out of the schema rather than retyped here.
///
/// <para>A capability model is a claim about a vocabulary, so the vocabulary has to come from the
/// one place that defines it. A hand-maintained list would be correct on the day it was written and
/// wrong the first time a block kind is added — and wrong in the worst direction, because a target
/// would silently accept something no emitter handles.</para>
/// </summary>
public static class DefinitionVocabulary
{
    private static readonly JsonObject Schema =
        JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!.AsObject();

    private static JsonObject Defs => Schema["$defs"]!.AsObject();

    /// <summary>Every <c>block_&lt;kind&gt;</c> the schema defines.</summary>
    public static readonly IReadOnlySet<string> BlockKinds =
        Defs.Where(d => d.Key.StartsWith("block_", StringComparison.Ordinal))
            .Select(d => d.Key["block_".Length..])
            .ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> EffectTypes = Enum("effect", "type");
    public static readonly IReadOnlySet<string> FieldTypes = Enum("field", "type");

    /// <summary>Trigger events, from <c>workflow.trigger.event</c> rather than from the flat
    /// <c>$defs</c> pattern the other two use.</summary>
    public static readonly IReadOnlySet<string> TriggerEvents =
        (Defs["workflow"]!["properties"]!["trigger"]!["properties"]!["event"]!["enum"]!.AsArray())
            .Select(v => v!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> Enum(string def, string property) =>
        (Defs[def]!["properties"]![property]!["enum"]!.AsArray())
            .Select(v => v!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
}
