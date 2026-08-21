// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <b>The question slice 5a leaves behind, split in two the way the answer needs it.</b>
///
/// <para><c>SynthesisRedundancyTests</c> in the runtime suite settles the first half: Cord cannot get
/// the corpus's UI for free by lowering to ABSENCE. The compiler's fallback builds one page per ENTITY
/// where the corpus authored one page per JOB, and turns every board and calendar into a table. So Cord
/// has to author screens.</para>
///
/// <para>Which makes the next question a measurement, not a design exercise — <c>upsert_screen</c>
/// already exists. But the measurement only means something when <b>LAYOUT and CONTENT are counted
/// apart</b>, because Cord answers them differently on purpose. Content is what the author is saying;
/// layout is how the lowerer arranges it. A tally that mixes them reports <c>columns</c> as a missing
/// vocabulary word when it is really a missing arrangement — and those have opposite fixes.</para>
///
/// <para><b>The first version of this test got that wrong twice over.</b> It did not descend through
/// <c>columns[][]</c> at all, so every columns block counted as a leaf and everything inside one was
/// invisible. Both errors pushed in the same direction: a container inflating the "cannot say" column
/// while its real content went uncounted.</para>
///
/// <para>Measured against what <see cref="CordSection"/> LOWERS to — <c>view</c>, <c>stat</c>,
/// <c>chart</c>, <c>text</c> — never against Cord's own words. Cord calls a stat a <c>metric</c>, and
/// comparing words understates coverage badly.</para>
///
/// <para>Inside the extractable unit deliberately: corpus and schema library only, so an open-source
/// copy of Cord can still ask itself this question.</para>
/// </summary>
public class CordScreenVocabularyTests(ITestOutputHelper output)
{
    /// <summary>What <see cref="CordLowerScreens"/> emits for a content section. Read from the
    /// lowering — if a section kind is added there, this is what has to move with it.</summary>
    private static readonly string[] Lowered = ["view", "stat", "chart", "text"];

    /// <summary>Every property through which a block holds other blocks, taken from the schema's own
    /// <c>$defs</c>: <c>blocks</c> on section/stack/row/grid/card/repeat/split, <c>columns</c> on
    /// columns (an array of arrays), and <c>tabs[].blocks</c>.</summary>
    private static void Walk(JsonObject block, Dictionary<string, int> containers, Dictionary<string, int> content)
    {
        var nested = false;

        void Into(JsonNode? node)
        {
            foreach (var child in (node as JsonArray ?? []).OfType<JsonObject>())
            {
                Walk(child, containers, content);
                nested = true;
            }
        }

        Into(block["blocks"]);
        // An array of ARRAYS — the level this test used to skip entirely.
        foreach (var column in (block["columns"] as JsonArray ?? []).OfType<JsonArray>()) Into(column);
        foreach (var tab in (block["tabs"] as JsonArray ?? []).OfType<JsonObject>()) Into(tab["blocks"]);

        if ((string?)block["kind"] is not { } kind) return;
        var into = nested ? containers : content;
        into[kind] = into.GetValueOrDefault(kind) + 1;
    }

    [Fact]
    public void What_the_screen_vocabulary_already_covers()
    {
        Dictionary<string, int> containers = new(StringComparer.Ordinal), content = new(StringComparer.Ordinal);
        foreach (string path in Corpus.SemanticCorpus())
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
            foreach (var page in (doc["pages"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var block in (page["blocks"] as JsonArray ?? []).OfType<JsonObject>())
                    Walk(block, containers, content);
        }

        var total = content.Values.Sum();
        var covered = content.Where(c => Lowered.Contains(c.Key)).Sum(c => c.Value);

        output.WriteLine($"CONTENT — what the author is saying: {covered}/{total} already sayable");
        foreach (var (kind, count) in content.OrderByDescending(c => c.Value))
            output.WriteLine($"{count,5}  {kind}{(Lowered.Contains(kind) ? "" : "   <- cannot say")}");

        output.WriteLine("");
        output.WriteLine($"LAYOUT — how it is arranged: {containers.Values.Sum()} containers, "
            + "none of which an author writes");
        foreach (var (kind, count) in containers.OrderByDescending(c => c.Value))
            output.WriteLine($"{count,5}  {kind}");

        // Stated at what was measured 2026-08-11, as a tripwire rather than a target: a CHANGE in either
        // direction is the signal. More coverage means a section kind was added; less means the corpus
        // grew a surface Cord cannot reach.
        Assert.Equal(ContentCovered, covered);
        Assert.Equal(ContentTotal, total);
        Assert.Equal(Containers, containers.Values.Sum());
    }

    // Measured 2026-08-11, AFTER the traversal was corrected. The uncorrected walk reported 165/252
    // with 21 `columns` blocks miscounted as content; descending through columns[][] moved those into
    // the layout column and revealed what was inside them. `chart` went from 4 to 42 — charts were the
    // thing most often placed side by side, so they were the thing most often invisible.
    //
    // Restated 2026-08-19, last of three passes that day: the budget planner's revenue model was
    // rebuilt on the Liquiditaetsplan's cohort engine, which traded a deferred-revenue chart for an
    // acquisition grid, a segment table, an adoption curve and a lifecycle table. 203/281 → 215/294.
    // Coverage held: every block the rebuild added is one Cord can already say.
    private const int ContentCovered = 215;
    private const int ContentTotal = 294;
    private const int Containers = 175;
}
