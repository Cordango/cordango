// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// One language's text for an app, keyed by what the authoring language SAYS rather than where it
/// sits. See <see cref="DefinitionText.Translate"/>.
///
/// <para>Keyed by text so a bundle survives what positions do not: a version that inserts a field, a
/// customer's edit to an installed template, the compiler reshaping the definition into a manifest.
/// Unchanged English keeps its translation wherever it moves. The cost is homonyms ("Open" the status
/// is "Offen", the button is "Öffnen"), which is what <see cref="At"/> is for.</para>
/// </summary>
/// <param name="Text">Authoring-language text → translation.</param>
/// <param name="At">Key path → translation, for the few strings whose text alone is ambiguous. Wins
/// over <paramref name="Text"/>.</param>
public sealed record AppTextBundle(IReadOnlyDictionary<string, string> Text, IReadOnlyDictionary<string, string> At)
{
    public static readonly AppTextBundle Empty = new(
        new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Read <c>{ "text": { … }, "at": { … } }</c>. Anything that is not a string pair is ignored.</summary>
    public static AppTextBundle Parse(JsonNode? bundle) =>
        bundle is JsonObject o ? new(Map(o["text"]), Map(o["at"])) : Empty;

    private static Dictionary<string, string> Map(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
            foreach (var (key, value) in o)
                if (value is JsonValue v && v.TryGetValue<string>(out var text)) map[key] = text;
        return map;
    }
}
