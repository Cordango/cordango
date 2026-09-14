// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class CrossAppSourceTests
{
    private static JsonObject Def(string blocks, string? uses = null)
    {
        var doc = (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"timesheets","name":"Timesheets","version":"1.0.0",
          "entities":[
            {"key":"time_entry","label":"Entry","labelPlural":"Entries","displayField":"notes","fields":[
              {"key":"notes","label":"Notes","type":"text"},
              {"key":"hours","label":"Hours","type":"decimal"}
            ]}
          ],
          "pages":[{"key":"my_week","label":"My week","entity":"time_entry","blocks":{{blocks}}}]
        }
        """)!;
        if (uses is not null) doc["uses"] = JsonNode.Parse(uses);
        return doc;
    }

    private const string ProjectRepeat = """
        [{"kind":"repeat","as":"proj","direction":"column",
          "source":{"app":"task_manager","entity":"project","sort":[{"field":"name","direction":"asc"}]},
          "blocks":[{"kind":"field","field":"name"}]}]
    """;

    private static KnownApps Workspace => KnownApps.Of([
        new KnownApp("task_manager", "Projects", ["project", "task", "milestone"]),
    ]);

    [Fact]
    public void Without_a_roster_a_cross_app_repeat_is_accepted_unchecked() =>
        Assert.Empty(Gate.SemanticErrors(Def(ProjectRepeat)));

    [Fact]
    public void With_a_roster_the_entity_resolves_in_the_other_app() =>
        Assert.Empty(Gate.SemanticErrors(Def(ProjectRepeat), Workspace));

    [Fact]
    public void An_entity_the_other_app_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"projekt"},
              "blocks":[{"kind":"field","field":"name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("unknown entity 'projekt' in app 'task_manager'"));
        Assert.Contains(errors, e => e.Contains("project"));
    }

    [Fact]
    public void A_workspace_without_the_other_app_refuses_the_source()
    {
        var errors = Gate.SemanticErrors(Def(ProjectRepeat), KnownApps.InWorkspace([]));

        Assert.Contains(errors, e => e.Contains("repeats app 'task_manager', which is not here"));
    }

    [Fact]
    public void A_tenant_lets_the_app_install_before_its_companion() =>
        Assert.Empty(Gate.SemanticErrors(Def(ProjectRepeat), KnownApps.InTenant([])));

    [Fact]
    public void A_core_app_entity_resolves_through_the_registry() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"org","source":{"app":"core_organizations","entity":"organization"},
              "blocks":[{"kind":"field","field":"name"}]}]
        """), Workspace));

    [Fact]
    public void A_core_app_entity_that_does_not_exist_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"org","source":{"app":"core_organizations","entity":"invoice"},
              "blocks":[{"kind":"field","field":"name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("unknown entity 'invoice' in core app 'core_organizations'"));
    }

    [Fact]
    public void Naming_this_app_itself_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"e","source":{"app":"timesheets","entity":"time_entry"},
              "blocks":[{"kind":"field","field":"notes"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("names this app itself"));
    }

    [Fact]
    public void The_platform_directory_is_an_origin_not_an_app()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"p","source":{"app":"platform","entity":"person"},
              "blocks":[{"kind":"field","field":"full_name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("the 'platform' origin, not an app"));
    }

    [Fact]
    public void A_table_over_another_apps_rows_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"table","source":{"app":"task_manager","entity":"project"},
              "columns":[{"field":"name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("source 'app' is on a"));
        Assert.Contains(errors, e => e.Contains("A table pages, sorts and writes"));
    }

    [Fact]
    public void A_board_over_another_apps_rows_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"board","source":{"app":"task_manager","entity":"task"},"groupBy":"status"}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("source 'app' is on a"));
    }

    [Fact]
    public void A_stat_may_reduce_another_apps_rows() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"stat","label":"Effort done",
              "source":{"app":"task_manager","entity":"task",
                        "filters":[{"field":"status","operator":"eq","value":"done"}],
                        "aggregate":{"op":"sum","field":"effort"}}}]
        """), Workspace));

    [Fact]
    public void A_chart_may_group_another_apps_rows() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"chart","label":"Effort by status","chartType":"bar",
              "source":{"app":"task_manager","entity":"task",
                        "aggregate":{"op":"sum","field":"effort","groupBy":"status"}}}]
        """), Workspace));

    [Fact]
    public void A_chart_over_an_entity_the_other_app_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"chart","label":"x","chartType":"bar",
              "source":{"app":"task_manager","entity":"tsak",
                        "aggregate":{"op":"count","groupBy":"status"}}}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("unknown entity 'tsak' in app 'task_manager'"));
    }

    [Fact]
    public void App_on_a_date_axis_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"d","source":{"app":"task_manager","dates":{"from":"{{today}}","count":7}},
              "blocks":[{"kind":"text","value":"{{d.label}}"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("has no meaning on a 'dates' axis"));
    }

    [Fact]
    public void Via_is_refused_because_the_bound_record_is_in_this_app()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"project","via":"owner"},
              "blocks":[{"kind":"field","field":"name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("source 'via' scopes rows to the record this block is bound to"));
    }

    [Fact]
    public void A_dotted_filter_path_is_refused_rather_than_matching_nothing()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj",
              "source":{"app":"task_manager","entity":"project",
                        "filters":[{"path":"lead.department","operator":"eq","value":"x"}]},
              "blocks":[{"kind":"field","field":"name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("hops a relation inside 'task_manager'"));
        Assert.Contains(errors, e => e.Contains("render empty rather than wrong"));
    }

    [Fact]
    public void A_plain_filter_on_the_other_apps_field_is_left_alone() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj",
              "source":{"app":"task_manager","entity":"project",
                        "filters":[{"field":"status","operator":"neq","value":"completed"}]},
              "blocks":[{"kind":"field","field":"name"},{"kind":"chip","field":"status"}]}]
        """), Workspace));

    [Fact]
    public void A_dotted_leaf_on_a_foreign_item_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"project"},
              "blocks":[{"kind":"field","field":"lead.full_name"}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("'lead.full_name' hops a relation inside 'task_manager'"));
    }

    [Fact]
    public void A_dotted_tint_on_a_foreign_item_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"project"},
              "blocks":[{"kind":"card","tint":"lead.tier","blocks":[{"kind":"field","field":"name"}]}]}]
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("hops a relation inside 'task_manager'"));
    }

    [Fact]
    public void A_nested_local_repeat_resets_the_foreign_item_rather_than_inheriting_it() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"project"},
              "blocks":[{"kind":"row","blocks":[
                {"kind":"field","field":"name"},
                {"kind":"repeat","as":"e","source":{"entity":"time_entry"},
                 "blocks":[{"kind":"field","field":"hours"}]}]}]}]
        """), Workspace));

    [Fact]
    public void A_cell_inside_a_cross_app_repeat_still_writes_this_apps_entity() =>
        Assert.Empty(Gate.SemanticErrors(Def("""
            [{"kind":"repeat","as":"proj","source":{"app":"task_manager","entity":"project"},
              "blocks":[{"kind":"cell","entity":"time_entry","field":"hours","editable":true,
                         "keys":{"notes":"{{proj.id}}"}}]}]
        """), Workspace));

    [Fact]
    public void An_undeclared_cross_app_repeat_is_reported_as_an_implicit_dependency()
    {
        var notes = Cordango.Compile.AppDependencies.Diagnose(Def(ProjectRepeat), Workspace);

        var note = Assert.Single(notes, n => n.Code == "dependency.implicit");
        Assert.Contains("task_manager", note.Message);
    }

    [Fact]
    public void A_declared_cross_app_repeat_reports_nothing()
    {
        var notes = Cordango.Compile.AppDependencies.Diagnose(
            Def(ProjectRepeat, """[{"app":"task_manager","entities":["project"],"why":"one project list"}]"""),
            Workspace);

        Assert.DoesNotContain(notes, n => n.Code is "dependency.implicit" or "dependency.unused");
    }
}
