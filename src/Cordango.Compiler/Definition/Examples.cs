// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <param name="Kind">The canonical block kind this is an example of.</param>
/// <param name="From">Where in the source app it was taken from, in words — "page 'Roadmap'".</param>
/// <param name="Block">The block itself: a real node from a definition that passes the gate.</param>
public sealed record BlockExample(string Kind, string From, JsonObject Block);

/// <summary>
/// A worked example of every block kind — what one CORRECTLY written looks like.
///
/// <para><b>Why this is not the schema.</b> <see cref="Schemas.AppDefinitionSchemaJson"/> says what
/// a block ACCEPTS, which is a different question from what a right one looks like, and the gap
/// between them is where authoring mistakes live. Six real ones, all made against a careful reading
/// of the schema: an empty <c>blocks: []</c> used as a spacer (<c>minItems</c> is 1), <c>cols</c> on
/// a form block where the property is <c>columns</c>, <c>limit</c> on a repeat instead of on its
/// source, a <c>stat</c> with no source outside a row context, a single-field <c>unique</c>
/// combination, and <c>format: "percent"</c> on a ratio, which appends a percent sign without
/// scaling. An example prevents every one of them; a schema prevents none.</para>
///
/// <para><b>Correct by construction.</b> These are not written by hand and reviewed. They are
/// extracted from a shipped application that passes the gate, by
/// <c>catalog/export_examples.py</c>, whose <c>--check</c> mode fails when the committed file has
/// drifted from its source. A hand-kept set of snippets goes stale the first time a property is
/// renamed; these cannot, because renaming the property breaks the app they come from.</para>
///
/// <para>Two kinds have no example on purpose: <c>widgets</c> is deprecated at authoring, and
/// <c>externalEmbed</c> needs a provider a platform admin has approved. Neither appears in the
/// source app, and the reason is recorded there rather than here.</para>
/// </summary>
public static class Examples
{
    private static readonly Lazy<IReadOnlyDictionary<string, BlockExample>> Loaded = new(Load);

    /// <summary>Every kind that has an example, in the order the file lists them.</summary>
    public static IReadOnlyCollection<string> Kinds => Loaded.Value.Keys.ToList();

    /// <summary>The example for one canonical block kind, or null when there is none.</summary>
    public static BlockExample? For(string? kind) =>
        kind is not null && Loaded.Value.TryGetValue(kind, out var e) ? e : null;

    /// <summary>The app the examples were taken from, for a caller that wants to say so.</summary>
    public static string Source { get; private set; } = "";

    private static IReadOnlyDictionary<string, BlockExample> Load()
    {
        var map = new Dictionary<string, BlockExample>(StringComparer.Ordinal);
        if (JsonNode.Parse(Schemas.ExamplesJson) is not JsonObject doc) return map;

        Source = doc["source"]?["app"]?.GetValue<string>() ?? "";
        foreach (var (kind, node) in doc["examples"] as JsonObject ?? [])
        {
            if (node is not JsonObject e || e["block"] is not JsonObject block) continue;
            map[kind] = new BlockExample(kind, e["from"]?.GetValue<string>() ?? "", block);
        }
        return map;
    }
}
