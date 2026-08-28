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
/// A repeat of cards, as the definition describes it rather than as a column of forms.
///
/// <para><b>Everything here was dropped in silence, which is what makes it worth a test.</b> The
/// projects screen asks for a three-across grid of bordered cards, each headed by the project name
/// in large bold type beside a status chip, with the lead's avatar under it. What came out was a
/// single column of page-wide cards, each field rendered as a grey caption above its value, and a
/// small chip where the avatar belonged saying the build could not draw one.</para>
///
/// <para>Only that last one was reported. <c>cols</c>, <c>wrap</c>, and every presentation property
/// on a <c>field</c> block reached the emitter and went nowhere — no output, no diagnostic — so the
/// screen looked finished and the definition looked ignored, with nothing to connect the two.</para>
/// </summary>
public class CardViewTests
{
    private const string Screen = "web/src/pages/ProjectsPage.vue";

    [Fact]
    public void The_repeat_asks_for_the_grid_the_definition_describes()
    {
        var page = Page();

        Assert.Contains(":wrap=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains(":cols=\"3\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void An_avatar_is_drawn_rather_than_apologised_for()
    {
        var page = Page();

        Assert.Contains("<BlockAvatar ", page, StringComparison.Ordinal);
        Assert.DoesNotContain("UnsupportedBlock kind=\"avatar\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_keeps_the_presentation_it_was_given()
    {
        var page = Page();

        // The card's heading: the project's name, large and bold, taking the room so the status chip
        // is pushed to the far end of the row.
        Assert.Contains(
            "<BlockField entity=\"project\" :record=\"record\" field=\"name\" size=\"lg\" weight=\"bold\" :grow=\"true\" />",
            page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_block_is_no_longer_a_one_column_form()
    {
        // RecordFields is the `fields` block and belongs to the detail pane. A `field` block reaching
        // for it is what turned every card title into the word "Name" above a name.
        Assert.DoesNotContain(":fields=\"['name']\" :columns=\"1\"", Page(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_carries_its_padding_and_its_border()
    {
        Assert.Contains("<BlockCard padding=\"md\" :bordered=\"true\">", Page(), StringComparison.Ordinal);
    }

    /// <summary>
    /// What is STILL missing says so.
    ///
    /// <para>The repeat's own filter bar and its <c>as</c> scope name are not emitted. Both used to
    /// vanish without a word, and the second is the worse of the two: a nested block naming the alias
    /// renders the token literally, so the screen shows <c>{{proj.name}}</c> to whoever opens it.</para>
    /// </summary>
    [Fact]
    public void The_parts_that_are_still_missing_are_reported()
    {
        var warnings = Build().Warnings.Select(d => d.Message).ToList();

        Assert.Contains(warnings, m => m.Contains("repeat's own filter bar", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("scope name", StringComparison.Ordinal));
    }

    private static string Page() =>
        Build().Files.Single(f => f.RelativePath == Screen).Content;

    private static GenerateResult Build()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", "task-manager.appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var outcome = CandidateValidator.Run(
            definition, "task-manager", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, "task-manager did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
