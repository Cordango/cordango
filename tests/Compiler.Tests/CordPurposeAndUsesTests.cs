// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordPurposeAndUsesTests
{
    private static JsonObject Definition() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion":"2.0","key":"crm","name":"CRM","version":"1.0.0",
      "purpose":{"summary":"Moves deals through the pipeline","duties":["owns the deal pipeline"]},
      "uses":[{"app":"core_organizations","entities":["organization"],"why":"customers live there"}],
      "entities":[
        {"key":"deal","label":"Deal","labelPlural":"Deals","displayField":"title","fields":[
          {"key":"title","label":"Title","type":"text"}
        ]}
      ]
    }
    """)!;

    private static CordApp Apply(CordApp draft, string opsJson)
    {
        var prepared = CordOps.Parse(JsonNode.Parse(opsJson), [.. CordOps.DomainOpNames]);
        Assert.Empty(prepared.Errors);
        var (app, errors) = CordOps.Apply(draft, prepared.Ops);
        Assert.Empty(errors);
        return app;
    }

    [Fact]
    public void A_purpose_survives_the_round_trip()
    {
        var app = CordImport.Import(Definition());

        Assert.Equal("Moves deals through the pipeline", app.Purpose!.Summary);
        Assert.Equal(["owns the deal pipeline"], app.Purpose.DutyList);
    }

    [Fact]
    public void Uses_survive_the_round_trip()
    {
        var use = Assert.Single(CordImport.Import(Definition()).UseList);

        Assert.Equal("core_organizations", use.App);
        Assert.Equal(["organization"], use.EntityList);
        Assert.Equal("customers live there", use.Why);
    }

    [Fact]
    public void Neither_lands_in_the_raw_remainder()
    {
        var app = CordImport.Import(Definition());

        Assert.False(app.Raw?.ContainsKey("purpose") ?? false);
        Assert.False(app.Raw?.ContainsKey("uses") ?? false);
    }

    [Fact]
    public void Lowering_reproduces_the_document_it_was_imported_from()
    {
        var original = Definition();

        var again = CordLower.Lower(CordImport.Import(original));

        Assert.Equal(original["purpose"]!.ToJsonString(), again["purpose"]!.ToJsonString());
        Assert.Equal(original["uses"]!.ToJsonString(), again["uses"]!.ToJsonString());
    }

    [Fact]
    public void An_app_that_declares_neither_lowers_without_them()
    {
        var bare = Definition();
        bare.Remove("purpose");
        bare.Remove("uses");

        var again = (JsonObject)CordLower.Lower(CordImport.Import(bare));

        Assert.False(again.ContainsKey("purpose"));
        Assert.False(again.ContainsKey("uses"));
    }

    [Fact]
    public void Set_purpose_states_it()
    {
        var app = Apply(CordImport.Import(Definition()), """
        [{"op":"set_purpose","purpose":{"summary":"Keeps the pipeline honest","duties":["owns deals"]}}]
        """);

        Assert.Equal("Keeps the pipeline honest", app.Purpose!.Summary);
    }

    [Fact]
    public void Set_purpose_replaces_rather_than_merges()
    {
        var app = Apply(CordImport.Import(Definition()), """
        [{"op":"set_purpose","purpose":{"summary":"Keeps the pipeline honest"}}]
        """);

        Assert.Empty(app.Purpose!.DutyList);
    }

    [Fact]
    public void Upsert_uses_declares_a_new_dependency()
    {
        var app = Apply(CordImport.Import(Definition()), """
        [{"op":"upsert_uses","use":{"app":"core_calendar","why":"meetings"}}]
        """);

        Assert.Equal(["core_organizations", "core_calendar"], app.UseList.Select(u => u.App));
    }

    [Fact]
    public void Upsert_uses_revises_the_declaration_it_already_has()
    {
        var app = Apply(CordImport.Import(Definition()), """
        [{"op":"upsert_uses","use":{"app":"core_organizations","entities":["organization","contact"]}}]
        """);

        var use = Assert.Single(app.UseList);
        Assert.Equal(["organization", "contact"], use.EntityList);
        Assert.Null(use.Why);
    }

    [Fact]
    public void Remove_uses_withdraws_the_declaration()
    {
        var app = Apply(CordImport.Import(Definition()), """
        [{"op":"remove_uses","app":"core_organizations"}]
        """);

        Assert.Empty(app.UseList);
        Assert.Null(app.Uses);
    }

    [Fact]
    public void Remove_uses_leaves_the_fields_that_point_there_alone()
    {
        var doc = Definition();
        ((JsonArray)doc["entities"]![0]!["fields"]!).Add(JsonNode.Parse("""
        {"key":"organization","label":"Company","type":"reference",
         "targetApp":"core_organizations","targetEntity":"organization"}
        """));

        var app = Apply(CordImport.Import(doc), """[{"op":"remove_uses","app":"core_organizations"}]""");

        Assert.Contains(app.EntityList[0].FieldList, f => f.Key == "organization");
    }

    [Fact]
    public void Withdrawing_a_declaration_that_was_never_made_is_refused()
    {
        var prepared = CordOps.Parse(JsonNode.Parse("""[{"op":"remove_uses","app":"core_calendar"}]"""),
            [.. CordOps.DomainOpNames]);
        var (_, errors) = CordOps.Apply(CordImport.Import(Definition()), prepared.Ops);

        Assert.Contains(errors, e => e.Message.Contains("core_calendar"));
    }

    [Fact]
    public void The_outline_states_the_purpose_and_what_the_app_builds_on()
    {
        var text = CordInspect.Describe(CordImport.Import(Definition()));

        Assert.Contains("Moves deals through the pipeline", text);
        Assert.Contains("owns: owns the deal pipeline", text);
        Assert.Contains("core_organizations (organization)", text);
    }
}
