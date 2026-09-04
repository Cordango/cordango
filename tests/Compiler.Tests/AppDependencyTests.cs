// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class AppDependencyTests
{
    private static JsonObject Def(string? uses = null, string fields = "")
    {
        var doc = (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"crm","name":"CRM","version":"1.0.0",
          "entities":[
            {"key":"deal","label":"Deal","labelPlural":"Deals","displayField":"title","fields":[
              {"key":"title","label":"Title","type":"text"}{{fields}}
            ]}
          ]
        }
        """)!;
        if (uses is not null) doc["uses"] = JsonNode.Parse(uses);
        return doc;
    }

    private const string OrgField = """
        ,{"key":"organization","label":"Company","type":"reference",
          "targetApp":"core_organizations","targetEntity":"organization"}
    """;

    private const string PersonField = """
        ,{"key":"owner","label":"Owner","type":"reference",
          "targetApp":"platform","targetEntity":"person"}
    """;

    [Fact]
    public void An_undeclared_reference_is_a_dependency_the_compiler_observed()
    {
        var deps = AppDependencies.Of(Def(fields: OrgField));

        var dep = Assert.Single(deps);
        Assert.Equal("core_organizations", dep.App);
        Assert.Equal(AppDependency.Reference, dep.Source);
        Assert.Equal(["organization"], dep.Entities);
        Assert.Equal(["deal.organization"], dep.Fields);
    }

    [Fact]
    public void A_declaration_nothing_points_at_is_declared_only()
    {
        var deps = AppDependencies.Of(Def(uses: """[{"app":"core_calendar"}]"""));

        var dep = Assert.Single(deps);
        Assert.Equal(AppDependency.Declared, dep.Source);
        Assert.Empty(dep.Fields);
    }

    [Fact]
    public void A_declaration_a_field_confirms_is_both()
    {
        var deps = AppDependencies.Of(Def(
            uses: """[{"app":"core_organizations","why":"customers live there"}]""",
            fields: OrgField));

        var dep = Assert.Single(deps);
        Assert.Equal(AppDependency.Both, dep.Source);
        Assert.Equal("customers live there", dep.Why);
    }

    [Fact]
    public void The_platform_directory_is_a_dependency_like_any_other()
    {
        var deps = AppDependencies.Of(Def(fields: PersonField));

        var dep = Assert.Single(deps);
        Assert.Equal("platform", dep.App);
        Assert.Equal(["person"], dep.Entities);
    }

    [Fact]
    public void Dependencies_come_back_in_a_stable_order()
    {
        var deps = AppDependencies.Of(Def(fields: PersonField + OrgField));

        Assert.Equal(["core_organizations", "platform"], deps.Select(d => d.App));
    }

    [Fact]
    public void An_undeclared_reference_is_reported_with_the_uses_entry_that_settles_it()
    {
        var note = Assert.Single(AppDependencies.Diagnose(Def(fields: OrgField)));

        Assert.Equal("dependency.implicit", note.Code);
        Assert.Equal(DefinitionNote.Note, note.Severity);
        Assert.Contains("core_organizations", note.Suggestion);
        Assert.Contains("organization", note.Suggestion);
    }

    [Fact]
    public void A_declared_dependency_nothing_uses_is_a_warning()
    {
        var note = Assert.Single(AppDependencies.Diagnose(Def(uses: """[{"app":"core_calendar"}]""")));

        Assert.Equal("dependency.unused", note.Code);
        Assert.Equal(DefinitionNote.Warning, note.Severity);
    }

    [Fact]
    public void The_platform_directory_is_never_reported_as_undeclared()
    {
        Assert.Empty(AppDependencies.Diagnose(Def(fields: PersonField)));
    }

    [Fact]
    public void A_declared_and_referenced_dependency_says_nothing()
    {
        Assert.Empty(AppDependencies.Diagnose(Def(
            uses: """[{"app":"core_organizations"}]""", fields: OrgField)));
    }

    [Fact]
    public void Diagnosis_never_edits_the_definition()
    {
        var doc = Def(fields: OrgField);
        var before = doc.ToJsonString();

        AppDependencies.Diagnose(doc);
        AppDependencies.Of(doc);

        Assert.Equal(before, doc.ToJsonString());
    }

    [Fact]
    public void Uses_may_not_name_the_platform_directory()
    {
        var errors = Gate.Validate(Def(uses: """[{"app":"platform"}]"""));

        Assert.Contains(errors, e => e.Contains("'platform'") && e.Contains("uses"));
    }

    [Fact]
    public void Uses_may_not_name_the_app_itself()
    {
        var errors = Gate.Validate(Def(uses: """[{"app":"crm"}]"""));

        Assert.Contains(errors, e => e.Contains("names this app itself"));
    }

    [Fact]
    public void Uses_may_not_name_one_app_twice()
    {
        var errors = Gate.Validate(Def(uses: """[{"app":"core_people"},{"app":"core_people"}]"""));

        Assert.Contains(errors, e => e.Contains("twice"));
    }

    [Fact]
    public void Uses_refuses_a_core_app_that_does_not_exist()
    {
        var errors = Gate.Validate(Def(uses: """[{"app":"core_invoices"}]"""));

        Assert.Contains(errors, e => e.Contains("core_invoices") && e.Contains("does not exist"));
    }

    [Fact]
    public void Uses_refuses_an_entity_a_core_app_does_not_have()
    {
        var errors = Gate.Validate(Def(
            uses: """[{"app":"core_organizations","entities":["invoice"]}]"""));

        Assert.Contains(errors, e => e.Contains("invoice"));
    }

    [Fact]
    public void Uses_accepts_a_core_app_and_its_real_entities()
    {
        var errors = Gate.Validate(Def(
            uses: """[{"app":"core_organizations","entities":["organization"]}]""",
            fields: OrgField));

        Assert.DoesNotContain(errors, e => e.Contains("uses"));
    }

    [Fact]
    public void Uses_accepts_another_tenant_app_the_gate_cannot_see()
    {
        var errors = Gate.Validate(Def(uses: """[{"app":"task_manager"}]"""));

        Assert.DoesNotContain(errors, e => e.Contains("task_manager"));
    }

    [Fact]
    public void A_purpose_is_carried_by_the_gate()
    {
        var doc = Def();
        doc["purpose"] = new JsonObject
        {
            ["summary"] = "Moves deals through the pipeline",
            ["duties"] = new JsonArray("owns the deal pipeline"),
        };

        Assert.DoesNotContain(Gate.Validate(doc), e => e.Contains("purpose"));
    }
}
