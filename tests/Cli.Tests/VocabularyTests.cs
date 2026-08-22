// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;

namespace Cordango.Cli.Tests;

public class VocabularyTests
{
    [Fact]
    public void It_answers_the_question_that_sent_an_agent_into_the_binary()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "block", "calendar");

        Assert.Equal(ExitCodes.Ok, exit);
        var properties = payload["schema"]!["properties"] as JsonObject;
        foreach (var setting in new[] { "startField", "endField", "colorField" })
            Assert.True(properties!.ContainsKey(setting), $"'{setting}' is what it was looking for");
    }

    [Fact]
    public void One_answer_is_a_fraction_of_the_schema_it_replaces()
    {
        using var sandbox = new Sandbox();

        var (_, payload) = sandbox.RunJson("vocabulary", "block", "calendar");

        Assert.InRange(payload["schema"]!.ToJsonString().Length, 100, 20_000);
    }

    [Fact]
    public void References_are_named_rather_than_inlined()
    {
        using var sandbox = new Sandbox();

        var (_, payload) = sandbox.RunJson("vocabulary", "block", "calendar");

        var references = (payload["references"] as JsonArray ?? []).Select(r => (string?)r).ToList();
        Assert.Contains("blockSource", references);
        Assert.Contains("#/$defs/blockSource", payload["schema"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_leads_with_Cord_words_because_that_is_what_most_files_are()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary");

        Assert.Equal(ExitCodes.Ok, exit);
        var cord = payload["cord"] as JsonObject;
        Assert.NotNull(cord);
        Assert.Contains("sectionKinds", cord!.Select(p => p.Key));

        Assert.Contains("calendar", (payload["blocks"] as JsonArray ?? []).Select(b => (string?)b));
    }

    [Fact]
    public void It_works_outside_a_workspace()
    {
        using var sandbox = new Sandbox();

        Assert.Equal(ExitCodes.Ok, sandbox.Run("vocabulary"));
        Assert.False(File.Exists(sandbox.Path_("cordango.yaml")));
    }

    [Fact]
    public void An_unknown_name_suggests_rather_than_shrugs()
    {
        using var sandbox = new Sandbox();

        var exit = sandbox.Run("vocabulary", "calendar");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("block_calendar", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_instructions_forbid_the_route_it_took()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        var instructions = File.ReadAllText(sandbox.Path_("CLAUDE.md"));

        Assert.Contains("cordango vocabulary", instructions, StringComparison.Ordinal);
        Assert.Contains("Never read the `cordango` binary", instructions, StringComparison.Ordinal);
    }
}
