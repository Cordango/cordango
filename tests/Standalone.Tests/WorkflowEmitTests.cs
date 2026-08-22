// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The definition's workflows reach the generated application.
///
/// <para>Each of these is a real workflow from the corpus, and each exercises a different piece of
/// the translation: a self-update on a field change, a cross-entity stamp through a reference, and an
/// insert into another entity on creation.</para>
/// </summary>
public class WorkflowEmitTests
{
    /// <summary>
    /// <c>sales-crm</c> sets a deal's probability when its stage changes — the commonest shape there
    /// is, and the one that needs <c>field.changed</c> to mean CHANGED rather than "was written".
    /// </summary>
    [Fact]
    public void A_field_change_writing_back_to_its_own_record()
    {
        var catalogue = Generated("sales-crm");

        Assert.Contains("WorkflowEvent.FieldChanged", catalogue, StringComparison.Ordinal);
        Assert.Contains("Field: \"stage\"", catalogue, StringComparison.Ordinal);
        Assert.Contains("When: Condition.Leaf(\"stage\", \"eq\", \"lead_in\")", catalogue, StringComparison.Ordinal);
        Assert.Contains("new UpdateRecordEffect([new EffectSet(\"probability\"", catalogue, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>helpdesk</c> stamps the first reply time onto the TICKET when a message is created.
    ///
    /// <para>The interesting part is <c>TargetEntity</c>. At run time the message holds a string id
    /// in its <c>ticket</c> field and nothing about that string says which table it belongs to — so
    /// the answer is resolved from the definition at build time and carried in the catalogue.</para>
    /// </summary>
    [Fact]
    public void A_stamp_that_lands_on_a_referenced_record()
    {
        var catalogue = Generated("helpdesk");

        Assert.Contains("WorkflowEvent.RecordCreated", catalogue, StringComparison.Ordinal);
        Assert.Contains("TargetField: \"ticket\"", catalogue, StringComparison.Ordinal);
        Assert.Contains("TargetEntity: \"ticket\"", catalogue, StringComparison.Ordinal);

        // setIfEmpty is what makes "first reply" mean the first one rather than the most recent.
        Assert.Contains("SetIfEmpty: true", catalogue, StringComparison.Ordinal);
    }

    /// <summary>An application with no workflows gets an empty catalogue rather than no file — the
    /// registration in <c>AppSetup</c> is unconditional, and a file that is sometimes absent would
    /// make it a compile error for half the corpus.</summary>
    [Fact]
    public void An_application_with_no_workflows_still_has_a_catalogue()
    {
        var catalogue = Generated("expenses");

        Assert.Contains("public static readonly AppWorkflowCatalogue Catalogue", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkflowDefinition(", catalogue, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows that ARE emitted stop being reported as missing.
    ///
    /// <para>A diagnostic that outlives its gap teaches whoever reads the build output to ignore the
    /// code, and the next real one goes with it.</para>
    /// </summary>
    [Theory]
    [InlineData("sales-crm")]
    [InlineData("helpdesk")]
    public void An_emitted_workflow_is_no_longer_reported_as_missing(string key)
    {
        Assert.DoesNotContain(Build(key).Warnings, w => w.Code == "CORD2302");
    }

    /// <summary>
    /// <c>crm</c> reminds an owner about a deal nobody has touched, at eight every morning.
    ///
    /// <para>The condition is what makes a schedule useful: the workflow runs over every deal and
    /// the condition decides which ones it is actually about. Neither half means anything
    /// alone.</para>
    /// </summary>
    [Fact]
    public void A_scheduled_workflow_carries_its_cron()
    {
        var catalogue = Generated("crm", corpus: "");

        Assert.Contains("WorkflowEvent.Schedule", catalogue, StringComparison.Ordinal);
        Assert.Contains("Cron: \"0 8 * * *\"", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain(Build("crm", corpus: "").Warnings, w => w.Code == "CORD2302");
    }

    /// <summary>
    /// Budget Planner lays its months out from a date range and its grids by crossing one entity
    /// with another — the two shapes of <c>createForEach</c>, and the reason the effect exists.
    /// </summary>
    [Fact]
    public void A_grid_is_laid_out_from_a_date_range_and_from_another_entity()
    {
        var catalogue = Generated("budget-planner", corpus: "");

        // Twelve months from the plan's start, both read off the record that triggered it.
        Assert.Contains(
            "new RangeSource(\"{{record.plan_start}}\", \"{{record.plan_months}}\", \"month\")",
            catalogue, StringComparison.Ordinal);

        // And a grid: one lifecycle step per adoption point of the same scenario.
        Assert.Contains("new EntitySource(\"adoption_point\"", catalogue, StringComparison.Ordinal);
        Assert.Contains(
            "new EffectFilter(\"scenario\", \"eq\", \"{{record.scenario}}\")",
            catalogue, StringComparison.Ordinal);

        // The key is what stops a second save laying the grid out on top of itself.
        Assert.Contains("[\"scenario\", \"sequence\"]", catalogue, StringComparison.Ordinal);

        // A looked-up value: the revenue plan for this scenario whose tier is flex.
        Assert.Contains("new PickValue(\"revenue_plan\"", catalogue, StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflow catalogue, from the emitter rather than from a whole build.
    ///
    /// <para>Budget Planner cannot be generated standalone at all — it places two <c>history</c>
    /// blocks, and record history is a platform capability rather than an unfinished emitter, so the
    /// build is REFUSED rather than merely partial. Its workflows are still the richest in the
    /// corpus and worth pinning, so this asks the emitter directly.</para>
    /// </summary>
    private static string Generated(string key, string corpus = "reference") =>
        WorkflowEmitter.Workflows(AppModel.From(Artifact(key, corpus))).Content;

    private static GenerateResult Build(string key, string corpus = "reference") =>
        new DotNetVueGenerator().Generate(new GenerateRequest(
            Artifact(key, corpus),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));

    private static CompiledAppArtifact Artifact(string key, string corpus)
    {
        var path = corpus.Length == 0
            ? Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", key + ".appdef.json")
            : Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", corpus, key + ".appdef.json");

        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
            outcome.Hash ?? "unhashed", new CompilerInfo("test", "1"));
    }
}
