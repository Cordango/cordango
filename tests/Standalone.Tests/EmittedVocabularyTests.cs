// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The emitter and the language must spell things the same way.
///
/// <para><b>Why this exists.</b> Three separate blocks shipped reading a key the language does not
/// have. A child list read <c>field</c> where the definition says <c>via</c>, so every child list in
/// every generated application asked the server to filter on a column named "" and rendered
/// <c>'segment' has no field ''</c> instead of rows. A text block read <c>text</c> where the
/// definition says <c>value</c>, so every explanatory paragraph came out blank. A chip block read
/// only <c>field</c>, so a chip carrying a literal rendered nothing. All three compiled, all three
/// passed every test there was, and all three were invisible until somebody built an application
/// and looked at it.</para>
///
/// <para>What they have in common is the shape of the failure rather than the block: a value the
/// definition wrote arrived at a component as nothing at all. So that is what is asserted here — an
/// attribute the emitter chose to write is never written empty, and a component whose whole job is
/// to show words is never emitted with none.</para>
/// </summary>
public class EmittedVocabularyTests
{
    /// <summary>Attributes that are legitimately empty: they carry "nothing here" as their
    /// meaning.</summary>
    private static readonly IReadOnlySet<string> MayBeEmpty =
        new HashSet<string>(StringComparer.Ordinal) { "class", "style" };

    /// <summary><c>name="value"</c> on a generated component. Bound props (<c>:name</c>) and
    /// handlers (<c>@name</c>) are skipped: those carry expressions rather than definition
    /// values.</summary>
    private static readonly Regex Attribute =
        new(@"(?<![:@\w-])(?<name>[a-zA-Z][a-zA-Z0-9-]*)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    private static readonly Regex TextComponent = new(@"<BlockText[\s/][^>]*>", RegexOptions.Compiled);

    private static readonly Regex CarriesWords = new(@"\stext=""[^""]+""", RegexOptions.Compiled);

    public static TheoryData<string> Applications()
    {
        var data = new TheoryData<string>();
        foreach (var key in new[]
        {
            "expenses", "time-off", "task-manager", "room-booking",
            "helpdesk", "sales-crm", "ventures", "budget-planner",
        }) data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(Applications))]
    public void No_generated_component_is_handed_an_empty_attribute(string key)
    {
        var offences = new List<string>();

        foreach (var file in Pages(key))
        {
            foreach (var line in file.Content.Split('\n'))
            {
                foreach (Match match in Attribute.Matches(line))
                {
                    if (match.Groups["value"].Value.Length > 0) continue;
                    if (MayBeEmpty.Contains(match.Groups["name"].Value)) continue;
                    offences.Add($"{file.RelativePath}: {line.Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0,
            $"{key} emitted components with an empty attribute, which is how a block reading the "
            + "wrong key fails. The emitter wrote the attribute, so it meant to carry something:"
            + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offences.Take(20)));
    }

    /// <summary>
    /// Prose survives the trip.
    ///
    /// <para>An empty attribute is only half the failure mode. <c>Attributes</c> drops a pair whose
    /// value is null, so a text block read through the wrong key emits <c>&lt;BlockText /&gt;</c>
    /// with nothing on it at all — no empty attribute to catch, just a paragraph that is not
    /// there.</para>
    ///
    /// <para>Counting authored blocks against emitted ones was the first attempt and it was wrong: a
    /// text block nested inside a container this target does not render — a <c>split</c>, say —
    /// correctly emits nothing, and the count called that a defect. The invariant that actually
    /// holds needs no ancestry.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Applications))]
    public void No_text_component_is_emitted_with_nothing_to_say(string key)
    {
        var empty = new List<string>();

        foreach (var file in Pages(key))
        {
            foreach (Match found in TextComponent.Matches(file.Content))
                if (!CarriesWords.IsMatch(found.Value))
                    empty.Add($"{file.RelativePath}: {found.Value.Trim()}");
        }

        Assert.True(empty.Count == 0,
            $"{key} emitted a text component with no words in it. A paragraph read through a key "
            + "the language does not use renders as a blank line:"
            + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", empty.Take(20)));
    }

    private static IReadOnlyList<GeneratedFile> Pages(string key) =>
        [.. Generate(key).Files.Where(f =>
            f.RelativePath.StartsWith("web/src/pages/", StringComparison.Ordinal))];

    private static JsonObject Definition(string key)
    {
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static GenerateResult Generate(string key)
    {
        var outcome = CandidateValidator.Run(
            Definition(key), key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(outcome.Manifest is not null,
            $"{key} did not compile: {string.Join("; ", outcome.Errors)}");

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        return new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
        {
            ["allowIncomplete"] = true,
            ["seed"] = 42,
        }));
    }
}
