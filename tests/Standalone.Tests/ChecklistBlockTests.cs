// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Standalone.Tests;

/// <summary>
/// A child list authored as a checklist comes out as one.
///
/// <para><b>What this replaces.</b> Every non-table presentation rendered as a table and said so at
/// CORD2308, which meant the onboarding screen showed six columns of which one mattered and asked
/// somebody to open a record to tick a box. The rows were right and the job was wrong — the one kind
/// of wrong a screenshot does not show.</para>
/// </summary>
public class ChecklistBlockTests
{
    private const string Screen = "web/src/pages/OnboardingRecordPage.vue";

    [Fact]
    public void The_checklist_is_drawn_rather_than_apologised_for()
    {
        var page = Page();

        Assert.Contains("<ChecklistBlock", page, StringComparison.Ordinal);
        Assert.Contains("import ChecklistBlock from '../blocks/ChecklistBlock.vue'", page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_tick_and_the_line_are_resolved_at_build_time()
    {
        var page = Page();

        Assert.Contains("done-field=\"done\"", page, StringComparison.Ordinal);
        Assert.Contains("title-field=\"title\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parent_narrows_it_the_way_every_other_child_list_is_narrowed()
    {
        Assert.Contains(
            "{'field':'onboarding','operator':'eq','value':'{{record.id}}'}", Page(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_reports_a_checklist_rendering_as_a_table()
    {
        var warnings = Build().Warnings.Select(d => d.Message).ToList();

        Assert.DoesNotContain(warnings,
            m => m.Contains("'checklist' presentation", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entity_with_nothing_to_tick_falls_back_and_says_why()
    {
        var result = Generate(WithoutTheBoolean());

        var page = result.Files.Single(f => f.RelativePath == Screen).Content;
        Assert.DoesNotContain("<ChecklistBlock", page, StringComparison.Ordinal);
        Assert.Contains("<ViewBlock", page, StringComparison.Ordinal);

        Assert.Contains(result.Warnings,
            d => d.Code == NotYetCodes.BlockOption
                && d.Message.Contains("no boolean field for a tick", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_booleans_still_draw_but_name_the_one_the_tick_writes()
    {
        var result = Generate(WithASecondBoolean());

        var page = result.Files.Single(f => f.RelativePath == Screen).Content;
        Assert.Contains("done-field=\"done\"", page, StringComparison.Ordinal);

        Assert.Contains(result.Warnings,
            d => d.Code == NotYetCodes.BlockOption
                && d.Message.Contains("writes 'done'", StringComparison.Ordinal)
                && d.Message.Contains("'billable'", StringComparison.Ordinal));
    }

    private static JsonObject WithoutTheBoolean()
    {
        var definition = Definition();
        Fields(definition).RemoveAt(IndexOf(definition, "done"));
        return definition;
    }

    private static JsonObject WithASecondBoolean()
    {
        var definition = Definition();
        Fields(definition).Add(new JsonObject
        {
            ["key"] = "billable",
            ["label"] = "Billable",
            ["type"] = "boolean",
        });
        return definition;
    }

    private static JsonArray Fields(JsonObject definition) =>
        (JsonArray)definition["entities"]!.AsArray()
            .First(e => (string?)e!["key"] == "onboarding_task")!["fields"]!;

    private static int IndexOf(JsonObject definition, string key)
    {
        var fields = Fields(definition);
        for (var i = 0; i < fields.Count; i++)
            if ((string?)fields[i]!["key"] == key) return i;

        throw new InvalidOperationException($"people-hr no longer has onboarding_task.{key}.");
    }

    private static string Page() =>
        Build().Files.Single(f => f.RelativePath == Screen).Content;

    private static GenerateResult Build() => Generate(Definition());

    private static JsonObject Definition()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", "people-hr.appdef.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static GenerateResult Generate(JsonObject definition)
    {
        var outcome = CandidateValidator.Run(
            definition, "people-hr", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, "people-hr did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
