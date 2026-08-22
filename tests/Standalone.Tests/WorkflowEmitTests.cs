// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

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
    /// A trigger this target does not wire is still reported.
    ///
    /// <para><c>crm</c> has a scheduled reminder, which needs a host that is awake and a timer to
    /// wake it. The build says so rather than producing an application whose reminder never
    /// arrives.</para>
    /// </summary>
    [Fact]
    public void A_scheduled_workflow_is_reported()
    {
        var reported = Build("crm", corpus: "").Warnings;

        Assert.Contains(reported, w => w.Code == "CORD2302" && w.Message.Contains("schedule", StringComparison.Ordinal));
    }

    private static string Generated(string key) =>
        Build(key).Files.Single(f => f.RelativePath == "api/Workflows/AppWorkflows.cs").Content;

    private static GenerateResult Build(string key, string corpus = "reference")
    {
        var path = corpus.Length == 0
            ? Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", key + ".appdef.json")
            : Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", corpus, key + ".appdef.json");

        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
                new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
