// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class KnownAppsTests
{
    private static JsonObject Def(string fields = "", string? uses = null)
    {
        var doc = (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"purchase_requests","name":"Purchase Requests","version":"1.0.0",
          "entities":[
            {"key":"purchase_request","label":"Request","labelPlural":"Requests","displayField":"title","fields":[
              {"key":"title","label":"Title","type":"text"}{{fields}}
            ]}
          ]
        }
        """)!;
        if (uses is not null) doc["uses"] = JsonNode.Parse(uses);
        return doc;
    }

    private const string BudgetLineField = """
        ,{"key":"budget_line","label":"Budget line","type":"reference",
          "targetApp":"budget_tracker","targetEntity":"budget_line"}
    """;

    private static KnownApps Workspace => KnownApps.Of([
        new KnownApp("budget_tracker", "Budget Tracker", ["budget", "budget_line", "spend_entry"]),
    ]);

    [Fact]
    public void Without_a_roster_a_sibling_reference_is_accepted_unchecked()
    {
        Assert.Empty(Gate.SemanticErrors(Def(BudgetLineField)));
    }

    [Fact]
    public void With_a_roster_a_sibling_reference_resolves()
    {
        Assert.Empty(Gate.SemanticErrors(Def(BudgetLineField), Workspace));
    }

    [Fact]
    public void An_app_that_is_not_in_the_roster_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"line","label":"Line","type":"reference",
              "targetApp":"nowhere","targetEntity":"thing"}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("references app 'nowhere', which is not here"));
    }

    [Fact]
    public void An_entity_the_sibling_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"line","label":"Line","type":"reference",
              "targetApp":"budget_tracker","targetEntity":"budgit_line"}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("unknown entity 'budgit_line' in app 'budget_tracker'"));
        Assert.Contains(errors, e => e.Contains("budget_line"));
    }

    [Fact]
    public void An_empty_workspace_is_a_caller_that_looked_and_found_none()
    {
        var errors = Gate.SemanticErrors(Def(BudgetLineField), KnownApps.InWorkspace([]));

        Assert.Contains(errors, e => e.Contains("references app 'budget_tracker', which is not here"));
    }

    [Fact]
    public void An_app_installs_into_a_tenant_that_does_not_have_its_companion_yet()
    {
        Assert.Empty(Gate.SemanticErrors(Def(BudgetLineField), KnownApps.InTenant([])));
    }

    [Fact]
    public void A_tenant_still_catches_an_entity_the_installed_app_does_not_have()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"line","label":"Line","type":"reference",
              "targetApp":"budget_tracker","targetEntity":"budgit_line"}
        """), KnownApps.InTenant([
            new KnownApp("budget_tracker", "Budget Tracker", ["budget", "budget_line"]),
        ]));

        Assert.Contains(errors, e => e.Contains("unknown entity 'budgit_line' in app 'budget_tracker'"));
    }

    [Fact]
    public void A_tenant_says_a_companion_is_not_installed_yet_rather_than_refusing()
    {
        var notes = Cordango.Compile.AppDependencies.Diagnose(
            Def(BudgetLineField, """[{"app":"budget_tracker"}]"""), KnownApps.InTenant([]));

        var note = Assert.Single(notes, n => n.Code == "dependency.absent");
        Assert.Contains("not installed here yet", note.Message);
    }

    [Fact]
    public void Core_apps_are_in_every_roster()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"vendor","label":"Vendor","type":"reference",
              "targetApp":"core_organizations","targetEntity":"organization"}
        """), KnownApps.Of([]));

        Assert.Empty(errors);
    }

    [Fact]
    public void A_core_app_is_still_checked_against_its_own_entities()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"vendor","label":"Vendor","type":"reference",
              "targetApp":"core_organizations","targetEntity":"nope"}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("unknown entity 'nope' in core app 'core_organizations'"));
    }

    [Fact]
    public void The_platform_directory_needs_no_roster()
    {
        var errors = Gate.SemanticErrors(Def("""
            ,{"key":"requester","label":"Requester","type":"reference",
              "targetApp":"platform","targetEntity":"person"}
        """), KnownApps.Of([]));

        Assert.Empty(errors);
    }

    [Fact]
    public void Uses_naming_a_sibling_in_the_roster_holds()
    {
        var errors = Gate.SemanticErrors(
            Def(BudgetLineField, """[{"app":"budget_tracker","entities":["budget_line"]}]"""), Workspace);

        Assert.Empty(errors);
    }

    [Fact]
    public void Uses_naming_an_entity_the_sibling_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(
            Def(BudgetLineField, """[{"app":"budget_tracker","entities":["ledger"]}]"""), Workspace);

        Assert.Contains(errors, e => e.Contains("`uses` says 'budget_tracker' has an entity 'ledger'"));
    }

    [Fact]
    public void Uses_naming_an_app_that_is_not_here_is_a_note_not_a_refusal()
    {
        var def = Def(uses: """[{"app":"nowhere"}]""");

        Assert.Empty(Gate.SemanticErrors(def, Workspace));

        var note = Assert.Single(Cordango.Compile.AppDependencies.Diagnose(def, Workspace));
        Assert.Equal("dependency.absent", note.Code);
        Assert.Contains("nowhere", note.Message);
    }

    [Fact]
    public void Uses_without_a_roster_still_stands()
    {
        Assert.Empty(Gate.SemanticErrors(Def(uses: """[{"app":"anything_at_all"}]""")));
    }

    [Fact]
    public void A_roster_entry_never_shadows_a_core_app()
    {
        var roster = KnownApps.Of([new KnownApp("core_organizations", "Mine", ["not_organization"])]);

        Assert.Empty(Gate.SemanticErrors(Def("""
            ,{"key":"vendor","label":"Vendor","type":"reference",
              "targetApp":"core_organizations","targetEntity":"organization"}
        """), roster));
    }
}
