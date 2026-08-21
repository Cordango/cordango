// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;

namespace Cordango.Cli.Tests;

/// <summary>
/// The core apps have to be FINDABLE, not merely referenceable.
///
/// <para><b>The failure these hold shut.</b> An agent was asked to link an app's tasks to the
/// workspace's organizations. The platform already provides Organizations to every workspace, the
/// gate has always accepted <c>targetApp: "core_organizations"</c>, and the runtime has always
/// resolved it. But nothing an author could run printed that the app existed: <c>cordango inspect</c>
/// listed only the workspace's own apps, <c>cordango vocabulary</c> never mentioned core apps, and the
/// <c>targetApp</c> description named 'platform' and then said "or that app's key" — which, read
/// from inside a workspace, means an app in that workspace. So the agent declared a second
/// organization entity. That was the correct inference from what it could see.</para>
///
/// <para><b>Discoverability, not capability, is what is asserted here.</b> The reference itself is
/// covered by <c>GateTests.Reference_to_core_app_entity_resolves</c>. These tests are about whether
/// an author can get from "I need organizations" to that reference without guessing.</para>
/// </summary>
public class CoreAppDiscoveryTests
{
    [Fact]
    public void The_first_call_an_agent_makes_names_the_core_apps()
    {
        // `cordango inspect` with no arguments is the workspace outline, and InspectCommand's own summary
        // calls it "the one call an agent makes first". If Organizations is not visible HERE, every
        // later surface is a recovery from a wrong turn already taken.
        using var sandbox = new Sandbox();
        sandbox.Run("new", "todo");

        var (exit, payload) = sandbox.RunJson("inspect");

        Assert.Equal(ExitCodes.Ok, exit);
        var core = payload["coreApps"] as JsonArray;
        Assert.NotNull(core);

        var organizations = core!.OfType<JsonObject>()
            .FirstOrDefault(a => (string?)a["systemKey"] == "core_organizations");
        Assert.NotNull(organizations);

        // The KEY, not the label. Organizations labels this entity "Company", and an author who is
        // shown only the label writes targetEntity: "company" and gets a gate error.
        Assert.Contains("organization",
            (organizations!["entities"] as JsonArray ?? []).Select(e => (string?)e));
    }

    [Fact]
    public void The_outline_says_to_link_rather_than_re_declare()
    {
        // Listing the apps is not enough on its own: the agent that failed had already decided the
        // concept was unmodelled. The instruction has to be in the same breath as the list.
        using var sandbox = new Sandbox();
        sandbox.Run("new", "todo");
        sandbox.Run("inspect");

        Assert.Contains("core_organizations", sandbox.Out, StringComparison.Ordinal);
        Assert.Contains("targetApp", sandbox.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_core_app_answers_for_itself()
    {
        // `cordango vocabulary core organizations` joins to the system key, which is deliberately the
        // exact string a reference has to carry.
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "core", "organizations");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal("core_organizations", (string?)payload["systemKey"]);

        var organization = (payload["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(e => (string?)e["key"] == "organization");
        Assert.NotNull(organization);

        // Fields too, because the author's next question is always whether the column they were
        // about to declare is already on the canonical record.
        Assert.Contains("website", (organization!["fields"] as JsonArray ?? []).Select(f => (string?)f));
    }

    [Fact]
    public void It_answers_without_a_workspace_or_an_instance()
    {
        // Core apps are provisioned server-side, which is exactly why an author cannot see them by
        // looking at the filesystem — and exactly why this answer must NOT need a connection. The
        // registry is embedded static data; CordyOSS §14 makes the offline guarantee non-negotiable.
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
        // This is the string that is prefix on every domain call, and the one that actively misled:
        // it enumerated the platform entities, organization was not among them, and the only other
        // hint was "that app's key". An agent reading it concluded the concept did not exist.
        using var sandbox = new Sandbox();

        var (exit, payload) = sandbox.RunJson("vocabulary", "operation", "upsert_field");

        Assert.Equal(ExitCodes.Ok, exit);
        var field = payload["schema"]!["$defs"]!["field"]!["properties"]!;
        var targetApp = (string?)field["targetApp"]!["description"] ?? "";

        Assert.Contains("core_organizations", targetApp, StringComparison.Ordinal);

        // And `target` has to say which ENTITY, because the platform list sitting right beside it is
        // what made organization look absent.
        Assert.Contains("organization", (string?)field["target"]!["description"] ?? "",
            StringComparison.Ordinal);
    }
}
