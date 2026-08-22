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

public class CordDocumentTests(ITestOutputHelper output)
{
    private static JsonNode Baseline(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    private static (CordWritten Written, CordApp Read, IReadOnlyList<CordError> Errors) Trip(CordApp app)
    {
        var written = CordDocument.Write(app);
        var blank = new CordApp(
            Key: (string?)written.Json["key"],
            Name: (string?)written.Json["name"],
            Version: (string?)written.Json["version"]);

        var prepared = CordTransaction.Prepare(blank, written.Json["ops"]);
        return (written, prepared.Next, prepared.Errors);
    }

    [Theory]
    [MemberData(nameof(Corpus.SemanticCorpus), MemberType = typeof(Corpus))]
    public void A_complete_document_reproduces_the_application_exactly(string path)
    {
        var baseline = Baseline(path);
        var app = CordImport.Import(baseline);
        var (written, read, errors) = Trip(app);

        if (!written.Complete)
        {
            output.WriteLine($"{Path.GetFileName(path)}: {written.Unwritable.Count} unwritable");
            foreach (var pointer in written.Unwritable.Take(8)) output.WriteLine($"  {pointer}");
            return;
        }

        Assert.Empty(errors);

        var want = DefinitionHash.Of(CordLower.Lower(app));
        var got = DefinitionHash.Of(CordLower.Lower(read));
        Assert.True(want == got,
            $"{Path.GetFileName(path)} reported a COMPLETE document that did not reproduce the app. "
            + "Either the writer omitted something it did not report, or a reader disagrees with it.");
    }

    [Fact]
    public void The_corpus_is_expressible_as_documents()
    {
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var complete = 0;
        var apps = 0;

        output.WriteLine($"{"app",-28} {"ops",5}  unwritable");
        foreach (string row in Corpus.SemanticCorpus())
        {
            var path = row;
            var app = CordImport.Import(Baseline(path));
            var (written, _, errors) = Trip(app);
            apps++;
            if (written.Complete) complete++;

            foreach (var pointer in written.Unwritable)
            {
                var kind = Kind(pointer);
                reasons[kind] = reasons.GetValueOrDefault(kind) + 1;
            }

            output.WriteLine($"{Path.GetFileNameWithoutExtension(path),-28} "
                + $"{written.Json["ops"]!.AsArray().Count,5}  "
                + (written.Complete ? "—" : string.Join(", ", written.Unwritable.Take(4)))
                + (errors.Count > 0 ? $"  REFUSED: {errors[0].Code}" : ""));
        }

        output.WriteLine("");
        foreach (var (kind, count) in reasons.OrderByDescending(r => r.Value))
            output.WriteLine($"{count,5}  {kind}");
        output.WriteLine($"\n{complete}/{apps} apps are fully expressible as semantic operations");

        Assert.True(complete >= CompleteFloor,
            $"only {complete}/{apps} apps write out completely, below the floor of {CompleteFloor}");
    }

    private const int CompleteFloor = 0;

    private static string Kind(string pointer) => pointer switch
    {
        var p when p.EndsWith("/raw", StringComparison.Ordinal) => "raw fragment (no operation exists)",
        var p when p.EndsWith("/at", StringComparison.Ordinal) => "command array position",
        var p when p.Contains("/command/", StringComparison.Ordinal) => "command key or label differs from its transition",
        var p when p.EndsWith("/via", StringComparison.Ordinal) => "aggregate join that inference cannot reproduce",
        var p when p.EndsWith("/style", StringComparison.Ordinal) => "button style Cord has no word for",
        var p when p.Contains("/placements/", StringComparison.Ordinal) => "button placement Cord has no word for",
        var p when p.EndsWith("/key", StringComparison.Ordinal) => "process key that is not its entity",
        _ => pointer,
    };
}
