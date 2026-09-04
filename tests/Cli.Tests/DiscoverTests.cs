// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;

namespace Cordango.Cli.Tests;

public class DiscoverTests
{
    private static Sandbox Workspace(string app = "claims")
    {
        var sandbox = new Sandbox();
        sandbox.Run("new", app);
        return sandbox;
    }

    private static IEnumerable<string?> Apps(JsonObject payload) =>
        (payload["apps"] as JsonArray ?? []).OfType<JsonObject>().Select(a => (string?)a["app"]);

    [Fact]
    public void An_unconnected_workspace_still_answers()
    {
        using var sandbox = Workspace();

        var (exit, payload) = sandbox.RunJson("discover");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains("claims", Apps(payload));
        Assert.Null((string?)payload["instance"]);
    }

    [Fact]
    public void The_core_apps_are_listed_without_a_network()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("discover");

        Assert.Contains((payload["coreApps"] as JsonArray ?? []).OfType<JsonObject>(),
            c => (string?)c["app"] == "core_organizations");
    }

    [Fact]
    public void An_app_reports_what_it_offers()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("discover", "--app", "claims");

        var app = Assert.Single((payload["apps"] as JsonArray ?? []).OfType<JsonObject>());
        Assert.NotEmpty(app["entities"] as JsonArray ?? []);
        Assert.NotEmpty(app["events"] as JsonArray ?? []);
    }

    [Fact]
    public void A_question_nothing_answers_returns_nothing()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("discover", "photosynthesis");

        Assert.Empty(Apps(payload));
    }

    [Fact]
    public void A_match_says_what_it_matched()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("discover", "claims");

        var app = Assert.Single((payload["apps"] as JsonArray ?? []).OfType<JsonObject>());
        Assert.NotEmpty(app["matched"] as JsonArray ?? []);
    }

    [Fact]
    public void A_section_narrows_the_question()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("discover", "claims", "--events");

        Assert.Equal("events", (string?)payload["section"]);
    }

    [Fact]
    public void It_tells_an_unconnected_author_what_they_are_not_seeing()
    {
        using var sandbox = Workspace();

        sandbox.Run("discover");

        Assert.Contains("not connected", sandbox.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_an_agent_reads_offers_it()
    {
        using var sandbox = Workspace();

        var (exit, payload) = sandbox.RunJson("help");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains((payload["commands"] as JsonArray ?? []).OfType<JsonObject>(),
            c => ((string?)c["usage"])?.StartsWith("discover", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void The_scaffold_tells_an_agent_to_run_it_before_modelling()
    {
        using var sandbox = Workspace();

        var instructions = File.ReadAllText(sandbox.Path_("AGENTS.md"));

        Assert.Contains("cordango discover", instructions, StringComparison.Ordinal);
    }
}
