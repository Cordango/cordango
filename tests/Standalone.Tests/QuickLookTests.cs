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
/// <c>openDetail</c>: a row read without leaving the list.
///
/// <para>The trip a list makes you take is the point of the feature — click a row, read one date,
/// press back, and lose your scroll position and your filters on the way. A definition asks for the
/// panel instead, and until now that request reached the emitter and produced a diagnostic and
/// nothing else.</para>
///
/// <para><b>The tables do it; the calendar, board and timeline still navigate.</b> That split is the
/// thing worth pinning, because the honest half is the easy one to lose: a shared "we do peek now"
/// flag would quietly stop reporting the three that do not.</para>
/// </summary>
public class QuickLookTests
{
    [Fact]
    public void A_table_that_asks_for_the_panel_gets_it()
    {
        var files = Build("sales-crm").Files;

        Assert.Contains(files, f =>
            f.RelativePath.StartsWith("web/src/pages/", StringComparison.Ordinal)
            && f.Content.Contains(":open-detail=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public void And_is_no_longer_reported_as_missing()
    {
        // sales-crm asks for the panel eight times: on seven tables, which now get it, and on one
        // board, which does not. Counting rather than asserting silence is what keeps this test
        // honest about the split — "no warnings" would pass just as well if the board stopped
        // reporting too.
        var reported = Build("sales-crm").Warnings
            .Count(d => d.Message.Contains("'openDetail'", StringComparison.Ordinal));

        Assert.Equal(1, reported);
    }

    [Fact]
    public void A_board_that_asks_for_it_still_says_it_cannot()
    {
        // task-manager's board tab sets openDetail. The board navigates, and saying nothing would be
        // the silent kind of gap this target refuses to leave.
        Assert.Contains(Build("task-manager").Warnings,
            d => d.Message.Contains("'openDetail'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_panel_component_ships_with_every_application()
    {
        Assert.Contains(Build("task-manager").Files,
            f => f.RelativePath == "web/src/blocks/RecordPeek.vue");
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
