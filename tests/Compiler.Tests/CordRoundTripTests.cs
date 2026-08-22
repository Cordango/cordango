// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class CordRoundTripTests
{
    private static JsonNode Baseline(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    private static JsonNode RoundTrip(JsonNode baseline) =>
        CordLower.Lower(CordImport.Import(baseline));

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void A_definition_survives_the_round_trip_unchanged(string path)
    {
        var baseline = Baseline(path);
        var roundTripped = RoundTrip(baseline);

        AssertSame(baseline, roundTripped, path);
    }

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
        Assert.True(Corpus.SemanticCorpus().Count >= 15,
            "expected the 13 reference apps + crm + budget-planner");
        Assert.True(Corpus.HistoricalDefinitions().Count >= 10,
            "expected the historical generated fixtures");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"key":"a"}""")]
    [InlineData("""{"entities":[]}""")]
    [InlineData("""{"key":"a","name":42}""")]
    [InlineData("""{"key":"a","name":null}""")]
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
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(null)).ToJsonString());
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(JsonNode.Parse("[]"))).ToJsonString());
        Assert.Equal("{}", CordLower.Lower(CordImport.Import(JsonNode.Parse("\"a string\""))).ToJsonString());
    }

    [Fact]
    public void Import_does_not_mutate_what_it_was_handed()
    {
        var doc = JsonNode.Parse("""{"key":"a","name":"A","entities":[{"key":"e"}]}""")!;
        var before = DefinitionHash.Of(doc);
        CordImport.Import(doc);
        Assert.Equal(before, DefinitionHash.Of(doc));
    }

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
        Assert.Null(app.Name);
        Assert.True(app.Raw!.ContainsKey("name"));
    }
}
