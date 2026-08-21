// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <b>Totality</b>: any App Definition survives the trip through the semantic model unchanged.
///
/// <para>Byte identity is the wrong test — <c>JsonNode</c> preserves neither key order nor
/// whitespace, and a definition round-trips through a <c>jsonb</c> column in production anyway.
/// <see cref="DefinitionHash"/> is the function that already answers "is this the same definition"
/// order-independently, and it is the one <c>finish</c> depends on, so reusing it means the codebase
/// has exactly one notion of sameness rather than a second one invented here.</para>
///
/// <para><b>This proves nothing on its own.</b> With a total raw overlay it passes on day one no
/// matter how little Cord understands. It is a SAFETY property — nothing is lost — and it is
/// <see cref="CordCoverageTests"/> that measures progress. Both are needed and neither substitutes
/// for the other.</para>
///
/// <para>RT‑2 (manifest identity) and RT‑3 (pipeline interchangeability) need the compiler and live in
/// <c>AppBuilder.Runtime.Tests/CordPipelineTests.cs</c>. Only these two run inside the extractable
/// unit, which is why they are the ones written here.</para>
/// </summary>
public class CordRoundTripTests
{
    /// <summary>
    /// The document as the pipeline would see it, before Cord touches anything.
    ///
    /// <para><c>Normalizer.Repair</c> and <c>AppSchemaVersion.Stamp</c> are the first two steps of
    /// <c>CandidateValidator.Run</c>. Applying them to BOTH sides means the corpus takes the identical
    /// path model output takes, so a difference here is Cord's and never normalisation's. Repair is
    /// documented as a no-op on canonical documents, so for the maintained corpus this is an
    /// identity.</para>
    /// </summary>
    private static JsonNode Baseline(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    private static JsonNode RoundTrip(JsonNode baseline) =>
        CordLower.Lower(CordImport.Import(baseline));

    // ---- RT-1: definition identity -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void A_definition_survives_the_round_trip_unchanged(string path)
    {
        var baseline = Baseline(path);
        var roundTripped = RoundTrip(baseline);

        AssertSame(baseline, roundTripped, path);
    }

    /// <summary>
    /// Hash equality, and when it fails, WHERE.
    ///
    /// <para>A bare hash comparison is the right assertion and a useless diagnostic: two SHA-256s tell
    /// you a document moved and nothing about what moved, on a document with tens of thousands of
    /// nodes. Walking the canonical forms to the first differing pointer turns a day of bisecting into
    /// a line of output — which is not a hypothetical, it is what this cost while the behaviour slice
    /// was being written.</para>
    /// </summary>
    private static void AssertSame(JsonNode baseline, JsonNode roundTripped, string path)
    {
        var want = DefinitionHash.Of(baseline);
        var got = DefinitionHash.Of(roundTripped);
        if (want == got) return;

        var differences = new List<string>();
        Walk(DefinitionHash.Canonical(baseline), DefinitionHash.Canonical(roundTripped), "", differences);

        Assert.Fail($"{Path.GetFileName(path)} did not survive the round trip.\n"
                    + string.Join("\n", differences.Take(12))
                    + (differences.Count > 12 ? $"\n… and {differences.Count - 12} more" : ""));
    }

    private static void Walk(JsonNode? want, JsonNode? got, string at, List<string> into)
    {
        if (into.Count >= 40) return;

        switch (want, got)
        {
            case (JsonObject a, JsonObject b):
                foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key), StringComparer.Ordinal))
                {
                    if (!a.ContainsKey(key)) { into.Add($"  + {at}/{key}  ADDED by lowering"); continue; }
                    if (!b.ContainsKey(key)) { into.Add($"  - {at}/{key}  LOST in the round trip"); continue; }
                    Walk(a[key], b[key], $"{at}/{key}", into);
                }
                return;

            case (JsonArray a, JsonArray b):
                if (a.Count != b.Count)
                {
                    into.Add($"  ~ {at}  length {a.Count} -> {b.Count}");
                    return;
                }
                for (var i = 0; i < a.Count; i++) Walk(a[i], b[i], $"{at}/{i}", into);
                return;

            default:
                var l = want?.ToJsonString() ?? "absent";
                var r = got?.ToJsonString() ?? "absent";
                if (l != r) into.Add($"  ~ {at}\n      want {Trim(l)}\n      got  {Trim(r)}");
                return;
        }

        static string Trim(string s) => s.Length <= 120 ? s : s[..120] + "…";
    }

    /// <summary>
    /// The same property over documents the gate REJECTS.
    ///
    /// <para>Not decoration. The WYSIWYG editor and refine can both write definitions that do not
    /// validate, and an authoring layer that can only read gate-clean documents cannot open the app a
    /// user just broke — which is precisely when they want help. These fixtures are a v1.0-era
    /// historical corpus and are asserted structurally elsewhere, never semantically; here they are
    /// the hardest available test of totality, because nobody tuned Cord against them.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus.HistoricalDefinitions), MemberType = typeof(Corpus))]
    public void A_definition_the_gate_rejects_still_survives_the_round_trip(string path)
    {
        var baseline = Baseline(path);
        Assert.Equal(DefinitionHash.Of(baseline), DefinitionHash.Of(RoundTrip(baseline)));
    }

    [Fact]
    public void The_corpora_are_found_and_non_trivial()
    {
        // Both [Theory] sets above would pass vacuously if discovery broke, and a suite that got
        // faster is not something anyone notices.
        Assert.True(Corpus.SemanticCorpus().Count >= 15,
            "expected the 13 reference apps + crm + budget-planner");
        Assert.True(Corpus.HistoricalDefinitions().Count >= 10,
            "expected the historical generated fixtures");
    }

    // ---- totality on documents nobody would call a definition -----------------------------------

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"key":"a"}""")]
    [InlineData("""{"entities":[]}""")]
    // `name` is a number, not a string: modelled properties are claimed only when they have the
    // expected SHAPE, so this one stays in the overlay and comes back a number.
    [InlineData("""{"key":"a","name":42}""")]
    [InlineData("""{"key":"a","name":null}""")]
    // Deeply odd, but still a JSON object — import may not throw on any of it.
    [InlineData("""{"key":{"nested":true}}""")]
    [InlineData("""{"":"empty key","a/b":"slash","c~d":"tilde"}""")]
    public void Import_is_total_over_anything_shaped_like_a_document(string json)
    {
        var doc = JsonNode.Parse(json)!;
        Assert.Equal(DefinitionHash.Of(doc), DefinitionHash.Of(RoundTrip(doc)));
    }

    [Fact]
    public void Import_declines_to_throw_on_things_that_are_not_documents()
    {
        // Null and non-objects reach this from real callers (a truncated payload, a stored `null`).
        // Lowering an empty model yields an empty object, which is as much as was there.
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(null)).ToJsonString());
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(JsonNode.Parse("[]"))).ToJsonString());
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(JsonNode.Parse("\"a string\""))).ToJsonString());
    }

    [Fact]
    public void Import_does_not_mutate_what_it_was_handed()
    {
        // The importer removes properties as it claims them. Doing that to the caller's node would
        // corrupt a draft on a failed apply — the exact bug the transaction exists to prevent.
        var doc = JsonNode.Parse("""{"key":"a","name":"A","entities":[{"key":"e"}]}""")!;
        var before = DefinitionHash.Of(doc);
        CordImport.Import(doc);
        Assert.Equal(before, DefinitionHash.Of(doc));
    }

    // ---- the model actually holds what it claims ------------------------------------------------

    [Fact]
    public void Modelled_properties_are_claimed_and_the_rest_is_carried()
    {
        var app = CordImport.Import(JsonNode.Parse("""
            {"key":"budget","name":"Budget","version":"2.0",
             "entities":[{"key":"line","label":"Line","fields":[]}],
             "pages":[{"key":"overview","label":"Overview","entity":"line","blocks":[
               {"kind":"view","view":"overview__lines"}]}],
             "views":[{"key":"overview__lines","label":"Lines","type":"table","entity":"line"}]}
            """));

        Assert.Equal("budget", app.Key);
        Assert.Equal("Budget", app.Name);
        Assert.Equal("2.0", app.Version);
        Assert.NotNull(app.Entities);
        Assert.NotNull(app.Screens);

        // What is modelled must NOT also be in the overlay: a property in both places round-trips
        // fine while scoring as covered, which is how a coverage number becomes decorative.
        foreach (var modelled in CordApp.Modelled)
            Assert.False(app.Raw?.ContainsKey(modelled) ?? false,
                $"'{modelled}' is modelled and must not also be raw");

        Assert.False(app.Raw?.ContainsKey("pages") ?? false);
        Assert.False(app.Raw?.ContainsKey("views") ?? false);
    }

    [Fact]
    public void A_wrongly_shaped_modelled_property_is_carried_rather_than_coerced()
    {
        var app = CordImport.Import(JsonNode.Parse("""{"key":"a","name":42}"""));

        Assert.Equal("a", app.Key);
        Assert.Null(app.Name);                       // not modelled — the shape was wrong
        Assert.True(app.Raw!.ContainsKey("name"));   // so it is carried, and survives
    }
}
