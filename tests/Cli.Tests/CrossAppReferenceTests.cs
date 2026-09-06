// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;

namespace Cordango.Cli.Tests;

public class CrossAppReferenceTests
{
    private static Sandbox TwoApps()
    {
        var sandbox = new Sandbox();
        sandbox.Run("new", "budget-tracker");
        sandbox.Run("add", "app", "purchase-requests");
        return sandbox;
    }

    private static void PointAt(Sandbox sandbox, string app, string entity)
    {
        var path = sandbox.Path_("apps", "purchase-requests", "entities", "task.cordango.yaml");
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\r', '\n') + $"""

  budget_line:
    label: Budget line
    type: reference
    targetApp: {app}
    targetEntity: {entity}

""");
    }

    private static JsonObject App(JsonObject payload, string key) =>
        payload["apps"]!.AsArray().OfType<JsonObject>().First(a => (string?)a["app"] == key);

    [Fact]
    public void A_reference_into_a_sibling_app_resolves()
    {
        using var sandbox = TwoApps();
        PointAt(sandbox, "budget_tracker", "task");

        var (exit, payload) = sandbox.RunJson("check");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.True((bool)App(payload, "purchase_requests")["coherent"]!, sandbox.Out);
    }

    [Fact]
    public void A_reference_into_a_sibling_is_reported_as_an_undeclared_dependency()
    {
        using var sandbox = TwoApps();
        PointAt(sandbox, "budget_tracker", "task");

        var (_, payload) = sandbox.RunJson("check");
        var notes = App(payload, "purchase_requests")["notes"]!.AsArray().OfType<JsonObject>();

        var note = Assert.Single(notes, n => (string?)n["code"] == "dependency.implicit");
        Assert.Contains("budget_tracker", (string)note["message"]!);
        Assert.Contains("uses: [{ app: budget_tracker", (string)note["suggestion"]!);
    }

    [Fact]
    public void An_entity_the_sibling_does_not_have_is_refused()
    {
        using var sandbox = TwoApps();
        PointAt(sandbox, "budget_tracker", "tsak");

        var (exit, payload) = sandbox.RunJson("check");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains(
            App(payload, "purchase_requests")["errors"]!.AsArray().Select(e => (string)e!),
            e => e.Contains("unknown entity 'tsak' in app 'budget_tracker'"));
    }

    [Fact]
    public void An_app_that_is_not_in_the_workspace_is_refused()
    {
        using var sandbox = TwoApps();
        PointAt(sandbox, "budgit_tracker", "task");

        var (exit, payload) = sandbox.RunJson("check");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains(
            App(payload, "purchase_requests")["errors"]!.AsArray().Select(e => (string)e!),
            e => e.Contains("references app 'budgit_tracker', which is not here"));
    }

    [Fact]
    public void Narrowing_to_one_app_still_resolves_its_siblings()
    {
        using var sandbox = TwoApps();
        PointAt(sandbox, "budget_tracker", "task");

        var (exit, payload) = sandbox.RunJson("check", "--app", "purchase_requests");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.True((bool)App(payload, "purchase_requests")["coherent"]!, sandbox.Out);
    }

    [Fact]
    public void A_single_app_workspace_still_accepts_a_core_reference()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var path = sandbox.Path_("apps", "claims", "entities", "task.cordango.yaml");
        File.WriteAllText(path, File.ReadAllText(path).TrimEnd('\r', '\n') + """

  vendor:
    label: Vendor
    type: reference
    targetApp: core_organizations
    targetEntity: organization

""");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
    }
}
