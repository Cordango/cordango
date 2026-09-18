// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

/// <summary>
/// Two apps of a workspace, linked into one application.
///
/// <para>The rule under test is that the DEFINITIONS never change. The same pair runs on Cordango
/// Platform, where each app keeps its own tables behind its own handle and two <c>dashboard</c>s are
/// fine; a standalone build is one deployment, so the BUILD renames and the author does not.</para>
/// </summary>
public class WorkspaceLinkTests
{
    [Fact]
    public void A_key_only_one_app_uses_is_left_alone()
    {
        var linked = Link(
            ("projects", App(entities: ["project"])),
            ("time", App(entities: ["timesheet"])));

        Assert.Equal(["project", "timesheet"], Keys(linked, "entities"));
    }

    [Fact]
    public void A_key_two_apps_share_is_qualified_in_BOTH()
    {
        var linked = Link(
            ("projects", App(entities: ["task"])),
            ("time", App(entities: ["task"])));

        Assert.Equal(["task_projects", "task_time"], Keys(linked, "entities"));
    }

    /// <summary>A page carries no address in the manifest — <c>PageModel.Route</c> derives one from
    /// the key. So renaming the key is what moves the screen, and there is nothing else to qualify.
    /// Both apps of the real `operations` workspace call a screen `dashboard`, which is what this
    /// whole link exists for.</summary>
    [Fact]
    public void Two_screens_of_the_same_name_each_keep_their_own_address()
    {
        var linked = Link(
            ("projects", App(pages: [("dashboard", null)])),
            ("time", App(pages: [("dashboard", null)])));

        Assert.Equal(["dashboard_projects", "dashboard_time"], Keys(linked, "pages"));
    }

    /// <summary>Every place the renamed key is NAMED has to move with it. A filter left pointing at
    /// the old key does not fail — it returns nothing, for ever.</summary>
    [Fact]
    public void Every_reference_to_a_renamed_entity_moves_with_it()
    {
        var projects = App(entities: ["task"]);
        projects["views"] = new JsonArray(new JsonObject { ["key"] = "open", ["entity"] = "task" });
        projects["pages"] = new JsonArray(new JsonObject
        {
            ["key"] = "board",
            ["route"] = "/board",
            ["entity"] = "task",
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["kind"] = "table",
                ["source"] = new JsonObject { ["entity"] = "task" },
            }),
        });

        var linked = Link(("projects", projects), ("time", App(entities: ["task"])));

        var view = linked.Manifest["views"]!.AsArray()[0]!;
        var page = linked.Manifest["pages"]!.AsArray()[0]!;

        Assert.Equal("task_projects", (string?)view["entity"]);
        Assert.Equal("task_projects", (string?)page["entity"]);
        Assert.Equal("task_projects", (string?)page["blocks"]![0]!["source"]!["entity"]);
    }

    /// <summary>The trap real manifests set: <c>via</c>, <c>groupBy</c>, <c>displayField</c> and
    /// <c>who</c> hold FIELD keys that happen to equal an entity key. A rewrite that matched on value
    /// would corrupt all four and nothing would report it.</summary>
    [Fact]
    public void A_field_key_that_looks_like_an_entity_key_is_not_touched()
    {
        var projects = App(entities: ["task"]);
        projects["pages"] = new JsonArray(new JsonObject
        {
            ["key"] = "board",
            ["route"] = "/board",
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["kind"] = "table",
                ["entity"] = "task",
                ["via"] = "task",
                ["groupBy"] = "task",
                ["displayField"] = "task",
            }),
        });

        var block = Link(("projects", projects), ("time", App(entities: ["task"])))
            .Manifest["pages"]!.AsArray()[0]!["blocks"]![0]!;

        Assert.Equal("task_projects", (string?)block["entity"]);
        Assert.Equal("task", (string?)block["via"]);
        Assert.Equal("task", (string?)block["groupBy"]);
        Assert.Equal("task", (string?)block["displayField"]);
    }

    /// <summary>Written by the calendar resolver and named in no obvious list. A link that missed it
    /// would put the record in nobody's calendar and say nothing.</summary>
    [Fact]
    public void The_calendar_binding_follows_its_entity()
    {
        var projects = App(entities: ["task"]);
        projects["entities"]!.AsArray()[0]!["calendar"] = new JsonObject
        {
            ["start"] = "due_on",
            ["who"] = "owner",
            ["whoEntity"] = "task",
        };

        var calendar = Link(("projects", projects), ("time", App(entities: ["task"])))
            .Manifest["entities"]!.AsArray()[0]!["calendar"]!;

        Assert.Equal("task_projects", (string?)calendar["whoEntity"]);
        Assert.Equal("owner", (string?)calendar["who"]);
    }

    /// <summary>A reference into a SIBLING becomes an ordinary local one: both apps are in this
    /// deployment, so the qualifier goes and the column is a column. This is what "the apps work
    /// together" means at the data layer.</summary>
    [Fact]
    public void A_reference_into_a_sibling_app_becomes_a_local_one()
    {
        var time = App(entities: ["timesheet"]);
        time["entities"]!.AsArray()[0]!["fields"] = new JsonArray(new JsonObject
        {
            ["key"] = "project",
            ["type"] = "reference",
            ["targetApp"] = "projects",
            ["targetEntity"] = "task",
        });

        var field = Link(("projects", App(entities: ["task"])), ("time", time))
            .Manifest["entities"]!.AsArray()[1]!["fields"]![0]!;

        Assert.Null(field["targetApp"]);
        Assert.Equal("task", (string?)field["targetEntity"]);
    }

    [Fact]
    public void A_reference_into_a_sibling_follows_that_apps_rename_not_this_ones()
    {
        var time = App(entities: ["task"]);
        time["entities"]!.AsArray()[0]!["fields"] = new JsonArray(new JsonObject
        {
            ["key"] = "of",
            ["type"] = "reference",
            ["targetApp"] = "projects",
            ["targetEntity"] = "task",
        });

        var field = Link(("projects", App(entities: ["task"])), ("time", time))
            .Manifest["entities"]!.AsArray()[1]!["fields"]![0]!;

        // The field lives in `time`, whose own `task` became task_time — but it points at the one in
        // `projects`, which became task_projects.
        Assert.Equal("task_projects", (string?)field["targetEntity"]);
    }

    /// <summary>A reference to something that is NOT in this build stays qualified and stays
    /// refusable. Dropping `targetApp` here would turn a reported gap into a dangling column.</summary>
    [Fact]
    public void A_reference_to_a_platform_entity_keeps_its_qualifier()
    {
        var projects = App(entities: ["task"]);
        projects["entities"]!.AsArray()[0]!["fields"] = new JsonArray(new JsonObject
        {
            ["key"] = "owner",
            ["type"] = "reference",
            ["targetApp"] = "platform",
            ["targetEntity"] = "person",
        });

        var field = Link(("projects", projects), ("time", App(entities: ["other"])))
            .Manifest["entities"]!.AsArray()[0]!["fields"]![0]!;

        Assert.Equal("platform", (string?)field["targetApp"]);
        Assert.Equal("person", (string?)field["targetEntity"]);
    }

    /// <summary>`process.state_entered` announces '&lt;entity&gt;.&lt;state&gt;', so renaming the
    /// entity renames the event. The only place in the manifest where a key hides inside a longer
    /// string — a subscription left on the old name waits for something nothing emits.</summary>
    [Fact]
    public void A_subscription_to_a_renamed_entitys_state_follows_the_rename()
    {
        // `time` carries a `task` of its own, so the rename is real and the subscription has to
        // follow the OTHER app's — which is the whole point of the test.
        var time = App(entities: ["task"]);
        time["workflows"] = new JsonArray(new JsonObject
        {
            ["key"] = "on_planned",
            ["trigger"] = new JsonObject
            {
                ["event"] = "process.state_entered",
                ["app"] = "projects",
                ["name"] = "task.planned",
            },
        });

        var trigger = Link(("projects", App(entities: ["task"])), ("time", time))
            .Manifest["workflows"]!.AsArray()[0]!["trigger"]!;

        Assert.Equal("task_projects.planned", (string?)trigger["name"]);
        Assert.Null(trigger["app"]);
    }

    /// <summary>The near-miss of the rule above. A `command.emitted` name reads identically —
    /// 'task.planned' — and is a name the AUTHOR chose in a command's `emits`. Rewriting it because
    /// its first word matches a renamed entity would break the very subscription it meant to fix.
    /// Seen for real: `register_on_project_planned` subscribes to 'project.planned'.</summary>
    [Fact]
    public void A_command_announcement_is_left_alone_even_when_its_head_was_renamed()
    {
        var time = App(entities: ["task"]);
        time["workflows"] = new JsonArray(new JsonObject
        {
            ["key"] = "on_planned",
            ["trigger"] = new JsonObject
            {
                ["event"] = "command.emitted",
                ["app"] = "projects",
                ["name"] = "task.planned",
            },
        });

        var trigger = Link(("projects", App(entities: ["task"])), ("time", time))
            .Manifest["workflows"]!.AsArray()[0]!["trigger"]!;

        Assert.Equal("task.planned", (string?)trigger["name"]);
    }

    /// <summary>A permission grant over every entity. '*' is not a key and must survive.</summary>
    [Fact]
    public void A_wildcard_grant_is_not_a_key()
    {
        var projects = App(entities: ["task"], roles: ["manager"]);
        projects["roles"]!.AsArray()[0]!["grants"] =
            new JsonArray(new JsonObject { ["entity"] = "*", ["read"] = true });

        var grant = Link(("projects", projects), ("time", App(entities: ["task"])))
            .Manifest["roles"]!.AsArray()[0]!["grants"]![0]!;

        Assert.Equal("*", (string?)grant["entity"]);
    }

    /// <summary>Roles follow the same rule as everything else, which is what gives two apps their own
    /// `manager`: managing projects is not managing timesheets.</summary>
    [Fact]
    public void Two_apps_that_both_declare_manager_get_one_each()
    {
        var linked = Link(
            ("projects", App(roles: ["manager"])),
            ("time", App(roles: ["manager"])));

        Assert.Equal(["manager_projects", "manager_time"], Keys(linked, "roles"));
    }

    /// <summary>A rename is a migration. Once a name has been given it does not move, whatever the
    /// collisions look like later — otherwise adding a third app renames a table in an app nobody
    /// touched, and it arrives as EF dropping a column.</summary>
    [Fact]
    public void A_name_already_assigned_is_kept_even_when_it_no_longer_collides()
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["entity:projects:task"] = "task_projects",
        };

        var linked = WorkspaceLink.Merge(
            new WorkspaceIdentity("ops", "Ops"),
            [("projects", App(entities: ["task"]))],
            previous);

        Assert.Equal(["task_projects"], Keys(linked, "entities"));
    }

    /// <summary>
    /// The scenario the map exists for, and the one a user actually meets: an app is deployed, a
    /// second app is added, and the second one has a `task` too.
    ///
    /// <para>The deployed app keeps the table it is running on and only the newcomer is qualified —
    /// asymmetric, deliberately. The symmetric answer would be tidier and would rename a table in a
    /// database somebody is using, which arrives as EF dropping a column.</para>
    /// </summary>
    [Fact]
    public void An_app_already_deployed_keeps_its_name_when_a_colliding_app_arrives()
    {
        // What the first build recorded, when `claims` was the only app.
        var deployed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["entity:claims:task"] = "task",
        };

        var linked = WorkspaceLink.Merge(
            new WorkspaceIdentity("ops", "Ops"),
            [("claims", App(entities: ["task"])), ("orders", App(entities: ["task"]))],
            deployed);

        Assert.Equal(["task", "task_orders"], Keys(linked, "entities"));
    }

    [Fact]
    public void The_map_records_every_decision_so_the_next_build_can_keep_it()
    {
        var linked = Link(
            ("projects", App(entities: ["task"])),
            ("time", App(entities: ["task"])));

        Assert.Equal("task_projects", linked.Names["entity:projects:task"]);
        Assert.Equal("task_time", linked.Names["entity:time:task"]);
    }

    /// <summary>Renaming cannot resolve this one: the runtime finds the form's parts by entity ROLE,
    /// so there is no key to qualify and nothing to tell two descriptors apart.</summary>
    [Fact]
    public void Two_apps_using_the_forms_archetype_is_refused_rather_than_renamed()
    {
        var a = App(entities: ["form"]);
        a["entities"]!.AsArray()[0]!["role"] = "formTemplate";
        var b = App(entities: ["survey"]);
        b["entities"]!.AsArray()[0]!["role"] = "formTemplate";

        var linked = WorkspaceLink.Merge(new WorkspaceIdentity("ops", "Ops"), [("a", a), ("b", b)]);

        var problem = Assert.Single(linked.Problems);
        Assert.Equal(DiagnosticCodes.WorkspaceKeyCollision, problem.Code);
        Assert.Contains("forms archetype", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_merged_application_is_named_after_the_workspace()
    {
        var linked = Link(("projects", App(entities: ["task"])), ("time", App(entities: ["other"])));

        Assert.Equal("ops", (string?)linked.Manifest["key"]);
        Assert.Equal("Ops", (string?)linked.Manifest["name"]);
    }

    private static LinkedWorkspace Link(params (string AppKey, JsonObject Manifest)[] apps) =>
        WorkspaceLink.Merge(new WorkspaceIdentity("ops", "Ops"), apps);

    private static IEnumerable<string?> Keys(LinkedWorkspace linked, string section) =>
        linked.Manifest[section]!.AsArray().Select(r => (string?)r!["key"]);

    private static JsonObject App(
        string[]? entities = null,
        (string Key, string? Route)[]? pages = null,
        string[]? roles = null) =>
        new()
        {
            ["entities"] = new JsonArray([.. (entities ?? []).Select(k =>
                (JsonNode)new JsonObject { ["key"] = k, ["fields"] = new JsonArray() })]),
            ["views"] = new JsonArray(),
            ["pages"] = new JsonArray([.. (pages ?? []).Select(p =>
                (JsonNode)new JsonObject { ["key"] = p.Key })]),
            ["workflows"] = new JsonArray(),
            ["roles"] = new JsonArray([.. (roles ?? []).Select(k => (JsonNode)new JsonObject { ["key"] = k })]),
        };
}
