// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.SourceGen;

/// <summary>
/// Asks one question: can THIS target build THIS application?
///
/// <para>It runs before a generator is invoked, so <c>cordango check --target standalone</c> costs a
/// walk of a JSON document and nothing else. That matters more than it sounds: a capability answer
/// that required generating would be too slow to put in a pre-commit hook, and a capability model
/// nobody runs is a capability model that drifts.</para>
///
/// <para><b>One implementation, every target.</b> Each generator declares what it supports; the walk
/// itself lives here. A generator that did its own walking would be free to check different things
/// from the one the CLI reports, and the first anybody would know is a build that passed
/// <c>check</c> and then failed.</para>
///
/// <para>The gate has already run by this point. Everything here assumes a structurally valid,
/// semantically coherent definition and reports only what a TARGET cannot do with it.</para>
/// </summary>
public static class TargetValidator
{
    public static IReadOnlyList<Diagnostic> Validate(JsonObject definition, GeneratorCapabilities caps)
    {
        var found = new List<Diagnostic>();

        Entities(definition, caps, found);
        Blocks(definition, caps, found);
        Effects(definition, caps, found);
        Workflows(definition, caps, found);

        // Ordered by where they appear rather than by when the walk happened to notice them: a
        // report that reads top-to-bottom through the document is one somebody can work down.
        return [.. found.OrderBy(d => d.JsonPath ?? "", StringComparer.Ordinal)
                        .ThenBy(d => d.Code, StringComparer.Ordinal)];
    }

    // ---- entities, fields, computed values ---------------------------------------------------

    private static void Entities(JsonObject definition, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        var entities = definition["entities"] as JsonArray ?? [];
        for (var e = 0; e < entities.Count; e++)
        {
            if (entities[e] is not JsonObject entity) continue;
            var entityPath = $"$.entities[{e}]";
            var key = Str(entity["key"]) ?? e.ToString();

            if (!caps.SeriesAndPrev && entity["series"] is not null)
                found.Add(new Diagnostic(DiagnosticCodes.SeriesEntity,
                    $"entity '{key}' declares a series, where each row's value depends on the row "
                    + "before it. This target computes each record on its own.",
                    entityPath + ".series"));

            var fields = entity["fields"] as JsonArray ?? [];
            for (var f = 0; f < fields.Count; f++)
            {
                if (fields[f] is JsonObject field)
                    Field(field, key, $"{entityPath}.fields[{f}]", caps, found);
            }
        }
    }

    private static void Field(
        JsonObject field, string entity, string path, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        var key = Str(field["key"]) ?? "?";
        var where = $"'{entity}.{key}'";

        if (Str(field["type"]) is { } type && !caps.FieldTypes.Allows(type))
            found.Add(new Diagnostic(DiagnosticCodes.UnsupportedFieldType,
                $"{where} is a '{type}' field: {caps.FieldTypes.Explain(type)}.", path));

        // A reference into another application. `targetApp` is the discriminator: absent means a
        // reference within this app, which every target can resolve.
        if (Str(field["targetApp"]) is { } targetApp)
        {
            if (!caps.PlatformTargets.Allows(targetApp))
                found.Add(new Diagnostic(DiagnosticCodes.CrossAppReference,
                    $"{where} points at '{targetApp}', a separately installed application: "
                    + $"{caps.PlatformTargets.Explain(targetApp)}.", path));
            else if (Str(field["targetEntity"]) is { } targetEntity
                     && !caps.PlatformEntities.Allows(targetEntity))
                found.Add(new Diagnostic(DiagnosticCodes.UnsupportedPlatformTarget,
                    $"{where} points at '{targetApp}.{targetEntity}': "
                    + $"{caps.PlatformEntities.Explain(targetEntity)}.", path));
        }

        if (field["computed"] is not JsonObject computed) return;

        // `prev(field)` reads the previous row of an ordered series, which only means anything where
        // the rows have an order the runtime maintains.
        if (!caps.SeriesAndPrev
            && Str(computed["expr"]) is { } expr
            && expr.Contains("prev(", StringComparison.Ordinal))
            found.Add(new Diagnostic(DiagnosticCodes.PrevExpression,
                $"{where} uses prev(), which reads the previous row of an ordered series. "
                + "This target computes each record on its own.", path + ".computed.expr"));

        if (!caps.WindowedRollups && computed["rollup"] is JsonObject rollup)
        {
            if (rollup["window"] is not null)
                found.Add(new Diagnostic(DiagnosticCodes.WindowedRollup,
                    $"{where} is a windowed rollup — it aggregates rows across a declared range "
                    + "rather than up a reference. This target supports rollups up a reference.",
                    path + ".computed.rollup.window"));
            else if (rollup["match"] is not null)
                found.Add(new Diagnostic(DiagnosticCodes.WindowedRollup,
                    $"{where} is a matched rollup — it aggregates siblings that share a parent "
                    + "rather than children of this record. This target supports rollups up a "
                    + "reference.", path + ".computed.rollup.match"));
        }
    }

    // ---- blocks -------------------------------------------------------------------------------

    /// <summary>
    /// Finds every block, wherever it is, and reports only kinds the schema calls blocks.
    ///
    /// <para><b>The whole document, not a list of sections.</b> The first version walked
    /// <c>pages</c> and <c>views</c>, which is where screens obviously live — and missed every block
    /// on an entity's own <c>detail</c>, <c>peek</c> and <c>form</c>. That is not a rare corner:
    /// the ventures app's audit block sits at
    /// <c>$.entities[0].detail.blocks[4].columns[1][0].tabs[4].blocks[0]</c>, so the target reported
    /// the app as buildable when it is not. An allow-list of locations is a list somebody has to
    /// keep complete, and being wrong in that direction is silent.</para>
    ///
    /// <para><b>One exclusion, and it is exact.</b> <c>kind</c> is not exclusively a block property —
    /// an entity has one too (<c>collection</c>, <c>config</c>, <c>settings</c>), and
    /// <c>settings</c> is spelled the same in both places. An entity is a direct element of the
    /// top-level <c>entities</c> array and nothing else is, so that one position is skipped rather
    /// than the whole subtree beneath it.</para>
    /// </summary>
    private static void Blocks(JsonObject definition, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        Walk(definition, "$", isEntity: false);

        void Walk(JsonNode? node, string path, bool isEntity)
        {
            switch (node)
            {
                case JsonObject o:
                    if (!isEntity
                        && Str(o["kind"]) is { } kind
                        && DefinitionVocabulary.BlockKinds.Contains(kind))
                        Block(kind, path, caps, found);

                    foreach (var (k, v) in o)
                        Walk(v, $"{path}.{k}", isEntity: false);
                    break;

                case JsonArray a:
                    // Elements of the top-level `entities` array, and only those, are entities.
                    var elementsAreEntities = path == "$.entities";
                    for (var i = 0; i < a.Count; i++)
                        Walk(a[i], $"{path}[{i}]", elementsAreEntities);
                    break;
            }
        }
    }

    private static void Block(string kind, string path, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        if (caps.Blocks.Allows(kind)) return;

        // Three block kinds get their own code because their reason is architectural rather than
        // "not built yet", and a person hitting one should be able to search for exactly it.
        var code = kind switch
        {
            "history" => DiagnosticCodes.HistoryBlock,
            "relatedApps" => DiagnosticCodes.RelatedAppsBlock,
            _ => DiagnosticCodes.UnsupportedBlock,
        };

        found.Add(new Diagnostic(code, $"'{kind}' block: {caps.Blocks.Explain(kind)}.", path));
    }

    // ---- effects and workflows -----------------------------------------------------------------

    private static void Effects(JsonObject definition, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        Walk(definition, "$");

        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "effects" && v is JsonArray effects)
                            for (var i = 0; i < effects.Count; i++)
                                if (effects[i] is JsonObject effect && Str(effect["type"]) is { } type)
                                    Effect(type, $"{path}.effects[{i}]", caps, found);
                        Walk(v, $"{path}.{k}");
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
            }
        }
    }

    private static void Effect(string type, string path, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        if (caps.Effects.Allows(type)) return;

        var code = type == "enrich" ? DiagnosticCodes.EnrichEffect : DiagnosticCodes.UnsupportedEffect;
        found.Add(new Diagnostic(code, $"'{type}' effect: {caps.Effects.Explain(type)}.", path));
    }

    private static void Workflows(JsonObject definition, GeneratorCapabilities caps, List<Diagnostic> found)
    {
        var workflows = definition["workflows"] as JsonArray ?? [];
        for (var w = 0; w < workflows.Count; w++)
        {
            if (workflows[w] is not JsonObject workflow) continue;
            if (Str(workflow["trigger"]?["event"]) is not { } evt) continue;
            if (caps.Triggers.Allows(evt)) continue;

            var key = Str(workflow["key"]) ?? w.ToString();
            found.Add(new Diagnostic(DiagnosticCodes.UnsupportedTrigger,
                $"workflow '{key}' triggers on '{evt}': {caps.Triggers.Explain(evt)}.",
                $"$.workflows[{w}].trigger"));
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
