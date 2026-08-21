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
/// <b>RT-4 — the semantic model survives being written down and read back.</b>
///
/// <para>The three round trips before this one all pass THROUGH the semantic model: import, lower,
/// compare. None of them ever asks whether the model itself can be serialized, because until
/// <see cref="CordDocument"/> it could not be — it existed in memory for the length of one design
/// session and was described in prose for the model to read.</para>
///
/// <para>This is the property a <c>.cord</c> file needs before it can be source rather than a view. If
/// writing an app out and reading it back does not reproduce the same App Definition, then the file is
/// a lossy export and the JSON stays canonical forever. If it does, the direction in the plan's rule 2
/// is reachable: <c>.cord</c> as source, App Definition demoted to a compiled artifact.</para>
///
/// <para><b>Two assertions, and the split is the honest part.</b> Where the writer reports the document
/// COMPLETE, the round trip must be exact — no tolerance, same <see cref="DefinitionHash"/> the gate
/// and <c>finish</c> use. Where it reports something unwritable, that is a measured gap with a floor,
/// not a failure: the authoring surface deliberately has no raw escape hatch (plan risk 3), so an app
/// carrying an importer escape hatch is one no author could have written in the first place.</para>
/// </summary>
public class CordDocumentTests(ITestOutputHelper output)
{
    private static JsonNode Baseline(string path)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
        AppSchemaVersion.Stamp(doc);
        return doc;
    }

    /// <summary>Write the model out, read the document back, and lower what came back.</summary>
    private static (CordWritten Written, CordApp Read, IReadOnlyList<CordError> Errors) Trip(CordApp app)
    {
        var written = CordDocument.Write(app);
        var blank = new CordApp(
            Key: (string?)written.Json["key"],
            Name: (string?)written.Json["name"],
            Version: (string?)written.Json["version"]);

        // Read by exactly the path a model's operations take. Not a second parser — if a document
        // needed one, the claim that a document IS an operation batch would be false.
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

        // An INCOMPLETE document is not expected to be readable, and that is the design rather than a
        // shortfall. What cannot be written is omitted, and omitting a required property leaves an
        // operation the schema refuses — a role grant with no entity, an automation with no trigger.
        // The alternative would be emitting a placeholder so the batch parses, which would turn a
        // reported gap into a document that reads back as a DIFFERENT application. Refusing loudly is
        // the better failure.
        if (!written.Complete)
        {
            output.WriteLine($"{Path.GetFileName(path)}: {written.Unwritable.Count} unwritable");
            foreach (var pointer in written.Unwritable.Take(8)) output.WriteLine($"  {pointer}");
            return;
        }

        // A COMPLETE document, though, must be one Cord accepts. Not implied by the hash comparison
        // below — a writer could emit something well-shaped that the checks refuse — and this is the
        // assertion that catches the two halves of the vocabulary drifting apart.
        Assert.Empty(errors);

        var want = DefinitionHash.Of(CordLower.Lower(app));
        var got = DefinitionHash.Of(CordLower.Lower(read));
        Assert.True(want == got,
            $"{Path.GetFileName(path)} reported a COMPLETE document that did not reproduce the app. "
            + "Either the writer omitted something it did not report, or a reader disagrees with it.");
    }

    /// <summary>
    /// How much of the corpus is expressible as a document, stated as a number that may not fall.
    ///
    /// <para>Same doctrine as the coverage floors: a measurement with a floor under it, raised only
    /// with evidence. What it measures is the distance between "Cord can read this app" and "Cord can
    /// SAY this app", and those are genuinely different — the importer has a raw overlay and the
    /// authoring surface deliberately does not.</para>
    /// </summary>
    [Fact]
    public void The_corpus_is_expressible_as_documents()
    {
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var complete = 0;
        var apps = 0;

        output.WriteLine($"{"app",-28} {"ops",5}  unwritable");
        // SemanticCorpus, not AllDefinitions: the latter sweeps in historical generator output and
        // bench scratch files, and a table measuring "how much of the corpus is expressible" that
        // includes _last_answers.json is measuring nothing.
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

    // Measured 2026-08-11 on the 15-app corpus: ZERO, and the zero is worth reading rather than
    // apologising for, because the reason is a single named thing.
    //
    // Every one of the 15 reports `/` — the app-level escape hatch — and every one of those is
    // `pages` + `views` plus three small top-level properties (`description`, `presentation`,
    // `theme`). Pages and views are not imported at all: CordCoverageTests asserts their coverage is
    // exactly zero, because the UI slice has not been built. So the distance between this corpus and
    // a corpus of .cord source files is not a long list of missing vocabulary — it is ONE slice.
    // timesheets is already there but for `/`.
    //
    // The next number to expect is therefore not 1 or 2. It is most of 15, the moment screens import.
    // Raise this when that lands; never lower it to make a change pass.
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
