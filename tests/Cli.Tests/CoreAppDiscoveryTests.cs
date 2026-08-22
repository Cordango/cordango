// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;

namespace Cordango.Cli.Tests;

public class CoreAppDiscoveryTests
{
    [Fact]
    public void The_first_call_an_agent_makes_names_the_core_apps()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "todo");

        var (exit, payload) = sandbox.RunJson("inspect");

        Assert.Equal(ExitCodes.Ok, exit);
        var core = payload["coreApps"] as JsonArray;
        Assert.NotNull(core);

        var organizations = core!.OfType<JsonObject>()
            .FirstOrDefault(a => (string?)a["systemKey"] == "core_organizations");
        Assert.NotNull(organizations);

        Assert.Contains("organization",
            (organizations!["entities"] as JsonArray ?? []).Select(e => (string?)e));
    }

    [Fact]
    public void The_outline_says_to_link_rather_than_re_declare()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "todo");
        sandbox.Run("inspect");

        Assert.Contains("core_organizations", sandbox.Out, StringComparison.Ordinal);
        Assert.Contains("targetApp", sandbox.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_core_app_answers_for_itself()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "core", "organizations");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal("core_organizations", (string?)payload["systemKey"]);

        var organization = (payload["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(e => (string?)e["key"] == "organization");
        Assert.NotNull(organization);

        Assert.Contains("website", (organization!["fields"] as JsonArray ?? []).Select(f => (string?)f));
    }

    [Fact]
    public void It_answers_without_a_workspace_or_an_instance()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "core", "organizations");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.NotNull(payload["entities"]);
        Assert.False(File.Exists(sandbox.Path_("cordango.yaml")));
    }

    [Fact]
    public void The_index_lists_them_beside_the_words()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary");

        Assert.Equal(ExitCodes.Ok, exit);
        var core = payload["coreApps"] as JsonObject;
        Assert.NotNull(core);
        Assert.Contains("core_organizations", core!.Select(p => p.Key));
    }

    [Fact]
    public void The_operation_schema_names_the_core_apps_it_used_to_omit()
    {
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "operation", "upsert_field");

        Assert.Equal(ExitCodes.Ok, exit);
        var field = payload["schema"]!["$defs"]!["field"]!["properties"]!;
        var targetApp = (string?)field["targetApp"]!["description"] ?? "";

        Assert.Contains("core_organizations", targetApp, StringComparison.Ordinal);

        Assert.Contains("organization", (string?)field["target"]!["description"] ?? "",
            StringComparison.Ordinal);
    }
}
