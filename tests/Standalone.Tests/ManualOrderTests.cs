// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// <c>orderField</c>: the number a list is arranged by, and that a drag rewrites.
///
/// <para>task-manager's task list has declared one since it was written. Nothing read it — not the
/// emitter, which never wrote the prop, and not the component, which had no such prop to take — so a
/// definition asking for a hand-ordered task list got an automatically ordered one, and the only
/// sign was a diagnostic saying so.</para>
///
/// <para><b>The split is the thing to pin.</b> A table can be reordered; a calendar, a board and a
/// timeline cannot — their arrangement is a date or a status, not a position — so for them this is
/// still a gap and still reported. One shared "we do ordering now" flag would silence all four.</para>
/// </summary>
public class ManualOrderTests
{
    [Fact]
    public void A_child_list_that_declares_an_order_gets_it()
    {
        var page = Build("task-manager").Files
            .Single(f => f.RelativePath == "web/src/pages/ProjectRecordPage.vue").Content;

        Assert.Contains("order-field=\"order\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void And_is_no_longer_reported_as_missing()
    {
        Assert.DoesNotContain(Build("task-manager").Warnings,
            d => d.Message.Contains("manual row order", StringComparison.Ordinal));
    }

    /// <summary>
    /// The section list is grouped by a REFERENCE, which is what makes its sections records somebody
    /// can add and rename. Pinned because the whole of section management hangs off it: grouped by a
    /// select instead, the same table would correctly offer no section controls at all.
    /// </summary>
    [Fact]
    public void The_task_list_groups_by_something_that_can_be_created()
    {
        var page = Build("task-manager").Files
            .Single(f => f.RelativePath == "web/src/pages/ProjectRecordPage.vue").Content;

        Assert.Contains("'field':'section'", page, StringComparison.Ordinal);

        var task = Build("task-manager").Files
            .Single(f => f.RelativePath == "web/src/app.js").Content;

        Assert.Contains("\"targetEntity\": \"task_section\"", task, StringComparison.Ordinal);
    }

    private static GenerateResult Build(string key)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var outcome = CandidateValidator.Run(
            definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, key + " did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
