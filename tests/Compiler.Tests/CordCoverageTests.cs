// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

public class CordCoverageTests(ITestOutputHelper output)
{
    private const double CorpusFloor = 0.30;

    private static JsonNode Load(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    [Fact]
    public void The_corpus_is_covered_to_at_least_the_floor()
    {
        var reports = Corpus.SemanticPaths()
            .Select(path => CordCoverage.Of(Load(path)))
            .OrderBy(r => r.App, StringComparer.Ordinal)
            .ToList();

        output.WriteLine($"{"app",-24} {"coverage",8}  nodes");
        foreach (var r in reports) output.WriteLine(r.ToString());

        var total = reports.Sum(r => r.TotalNodes);
        var raw = reports.Sum(r => r.RawNodes);
        var fraction = total == 0 ? 1.0 : 1.0 - (double)raw / total;
        output.WriteLine($"\nCORPUS {fraction * 100:F2}%  ({total - raw}/{total} nodes over {reports.Count} apps)");

        Assert.True(fraction >= CorpusFloor,
            $"corpus coverage {fraction * 100:F2}% is below the floor of {CorpusFloor * 100:F2}%");
    }

    [Fact]
    public void Coverage_is_reported_per_section()
    {
        string[] sections = ["/entities", "/pages", "/views", "/processes", "/commands", "/workflows", "/roles"];
        var totals = sections.ToDictionary(s => s, _ => (Total: 0, Raw: 0));
        var (domainTotal, domainRaw) = (0, 0);

        foreach (var path in Corpus.SemanticPaths())
        {
            var lowering = CordLower.Lowering(CordImport.Import(Load(path)));

            foreach (var section in sections)
            {
                var (t, r) = CordCoverage.Section(lowering, section);
                totals[section] = (totals[section].Total + t, totals[section].Raw + r);
            }

            var (dt, dr) = CordCoverage.Section(lowering, "/entities", "detail", "peek", "form");
            domainTotal += dt;
            domainRaw += dr;
        }

        output.WriteLine($"{"section",-28} {"coverage",8}  nodes");
        void Line(string label, int total, int raw) => output.WriteLine(
            $"{label,-28} {(total == 0 ? 100.0 : 100.0 - 100.0 * raw / total),7:F1}%  ({total - raw}/{total})");

        Line("entities (whole subtree)", totals["/entities"].Total, totals["/entities"].Raw);
        Line("  entities: domain only", domainTotal, domainRaw);
        foreach (var s in sections.Skip(1)) Line(s[1..], totals[s].Total, totals[s].Raw);

        Assert.True(1.0 - (double)domainRaw / domainTotal >= 0.99, "entity domain coverage below 99%");

        foreach (var untouched in new[] { "/pages", "/views" })
            Assert.Equal(0, totals[untouched].Total - totals[untouched].Raw);

        void Floor(string section, double floor)
        {
            var (total, raw) = totals[section];
            var actual = 1.0 - (double)raw / total;
            Assert.True(actual >= floor,
                $"{section} coverage {actual:P1} fell below its {floor:P0} floor ({total - raw}/{total})");
        }

        Floor("/processes", 0.98);
        Floor("/commands", 1.00);
        Floor("/workflows", 0.96);
        Floor("/roles", 0.97);
    }

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void Nothing_is_modelled_and_overlaid_at_once(string path)
    {
        var report = CordCoverage.Of(Load(path));
        Assert.True(report.Overlaps.Count == 0,
            $"{report.App}: modelled AND raw at {string.Join(", ", report.Overlaps)} — "
            + "the raw copy would win, so this would round-trip while scoring as covered");
    }

    [Fact]
    public void The_raw_escape_hatches_are_exactly_the_reviewed_list()
    {
        var path = Path.Combine(Corpus.RepoRoot(), "tests", "fixtures", "raw-allowlist.json");
        Assert.True(File.Exists(path), $"the raw allowlist is missing at {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var scope = doc.RootElement.GetProperty("scope");
        var prefixes = scope.GetProperty("prefixes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var ignoring = scope.GetProperty("ignoringSegments").EnumerateArray()
            .Select(x => x.GetString()!).ToArray();

        var allowed = new List<string>();
        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            foreach (var required in new[] { "app", "pointer", "reason" })
                Assert.True(entry.TryGetProperty(required, out var v) && v.GetString() is { Length: > 0 },
                    $"every allowlist entry needs a non-empty '{required}' — the reason is for the "
                    + "human reading the diff, and an entry without one is just a silenced test");
            allowed.Add($"{entry.GetProperty("app").GetString()}{entry.GetProperty("pointer").GetString()}");
        }

        var actual = new List<string>();
        foreach (var file in Corpus.SemanticPaths())
        {
            var definition = Load(file);
            var app = (definition as JsonObject)?["key"]?.GetValue<string>() ?? "(unkeyed)";
            var lowering = CordLower.Lowering(CordImport.Import(definition));

            actual.AddRange(lowering.RawPointers
                .Where(p => prefixes.Any(x => p.StartsWith(x + "/", StringComparison.Ordinal)))
                .Where(p => !ignoring.Any(seg => p.EndsWith("/" + seg, StringComparison.Ordinal)))
                .Select(p => app + p));
        }

        var unreviewed = actual.Except(allowed, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var stale = allowed.Except(actual, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (unreviewed.Count > 0 || stale.Count > 0)
            Assert.Fail(
                (unreviewed.Count > 0
                    ? $"{unreviewed.Count} raw pointer(s) with no reviewed reason:\n"
                      + string.Join("\n", unreviewed.Select(x => "  + " + x)) + "\n"
                    : "")
                + (stale.Count > 0
                    ? $"{stale.Count} allowlist entr(y/ies) no longer raw — modelling improved, so "
                      + "delete these lines:\n" + string.Join("\n", stale.Select(x => "  - " + x))
                    : ""));
    }

    [Fact]
    public void Coverage_is_one_when_everything_is_modelled_and_falls_with_what_is_carried()
    {
        var modelled = CordCoverage.Of(JsonNode.Parse(
            """{"key":"a","name":"A","entities":[{"key":"e","label":"E","fields":[{"key":"name","label":"Name","type":"text","required":true}]}]}"""));
        Assert.Empty(modelled.RawPointers);
        Assert.Equal(1.0, modelled.Fraction);

        var carried = CordCoverage.Of(JsonNode.Parse(
            """{"key":"a","pages":[{"key":"p","label":"P","blocks":[{"kind":"text","value":"x"}]}]}"""));
        Assert.Equal(["/pages"], carried.RawPointers);
        Assert.True(carried.Fraction < 0.3, $"expected the page subtree to dominate, got {carried.Percent:F1}%");
    }

    [Fact]
    public void An_empty_document_is_covered_rather_than_uncovered()
    {
        Assert.Equal(1.0, CordCoverage.Of(JsonNode.Parse("{}")).Fraction);
    }
}
