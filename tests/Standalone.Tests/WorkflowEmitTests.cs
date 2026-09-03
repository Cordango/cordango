// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

public class WorkflowEmitTests
{
    [Fact]
    public void A_field_change_writing_back_to_its_own_record()
    {
        var catalogue = Generated("sales-crm");

        Assert.Contains("WorkflowEvent.FieldChanged", catalogue, StringComparison.Ordinal);
        Assert.Contains("Field: \"stage\"", catalogue, StringComparison.Ordinal);
        Assert.Contains("When: Condition.Leaf(\"stage\", \"eq\", \"lead_in\")", catalogue, StringComparison.Ordinal);
        Assert.Contains("new UpdateRecordEffect([new EffectSet(\"probability\"", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stamp_that_lands_on_a_referenced_record()
    {
        var catalogue = Generated("helpdesk");

        Assert.Contains("WorkflowEvent.RecordCreated", catalogue, StringComparison.Ordinal);
        Assert.Contains("TargetField: \"ticket\"", catalogue, StringComparison.Ordinal);
        Assert.Contains("TargetEntity: \"ticket\"", catalogue, StringComparison.Ordinal);

        Assert.Contains("SetIfEmpty: true", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_with_no_workflows_still_has_a_catalogue()
    {
        var catalogue = Generated("expenses");

        Assert.Contains("public static readonly AppWorkflowCatalogue Catalogue", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkflowDefinition(", catalogue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sales-crm")]
    [InlineData("helpdesk")]
    public void An_emitted_workflow_is_no_longer_reported_as_missing(string key)
    {
        Assert.DoesNotContain(Build(key).Warnings, w => w.Code == "CORD2302");
    }

    [Fact]
    public void A_scheduled_workflow_carries_its_cron()
    {
        var catalogue = Generated("crm", corpus: "");

        Assert.Contains("WorkflowEvent.Schedule", catalogue, StringComparison.Ordinal);
        Assert.Contains("Cron: \"0 8 * * *\"", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain(Build("crm", corpus: "").Warnings, w => w.Code == "CORD2302");
    }

    [Fact]
    public void A_grid_is_laid_out_from_a_date_range_and_from_another_entity()
    {
        var catalogue = Generated("budget-planner", corpus: "");

        Assert.Contains(
            "new RangeSource(\"{{record.plan_start}}\", \"{{record.plan_months}}\", \"month\")",
            catalogue, StringComparison.Ordinal);

        Assert.Contains("new EntitySource(\"adoption_point\"", catalogue, StringComparison.Ordinal);
        Assert.Contains(
            "new EffectFilter(\"scenario\", \"eq\", \"{{record.scenario}}\")",
            catalogue, StringComparison.Ordinal);

        Assert.Contains("[\"scenario\", \"sequence\"]", catalogue, StringComparison.Ordinal);

        Assert.Contains("new PickValue(\"revenue_plan\"", catalogue, StringComparison.Ordinal);
    }

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
