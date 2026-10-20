// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class IntegrationCordTests
{
    private static JsonObject Definition() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion":"2.0","key":"sales","name":"Sales","version":"1.0.0",
      "uses":[
        {"app":"core_organizations","entities":["organization"],"why":"customers live there"},
        {"integration":"hubspot.crm@1","as":"crm_eu","why":"European contacts"},
        {"integration":"hubspot.crm@1","as":"crm_us"}
      ],
      "entities":[
        {"key":"deal","label":"Deal","labelPlural":"Deals","displayField":"title","fields":[
          {"key":"title","label":"Title","type":"text"}
        ]}
      ]
    }
    """)!;

    [Fact]
    public void Integration_uses_are_modelled_rather_than_carried_raw()
    {
        var app = CordImport.Import(Definition());

        Assert.Equal(3, app.UseList.Count);
        var eu = app.UseList[1];
        Assert.Null(eu.App);
        Assert.Equal("hubspot.crm@1", eu.Integration);
        Assert.Equal("crm_eu", eu.As);
        Assert.Equal("European contacts", eu.Why);
        Assert.False(app.Raw?.ContainsKey("uses") ?? false);
    }

    [Fact]
    public void They_lower_back_to_the_same_document()
    {
        var lowered = CordLower.Lower(CordImport.Import(Definition()));

        Assert.Equal(DefinitionHash.Of(Definition()["uses"]), DefinitionHash.Of(lowered["uses"]));
    }

    [Fact]
    public void Upserting_an_app_use_leaves_the_integrations_alone()
    {
        var draft = CordImport.Import(Definition());
        var prepared = CordOps.Parse(JsonNode.Parse("""
            [{"op":"upsert_uses","use":{"app":"core_people","why":"owners"}}]
            """), [.. CordOps.DomainOpNames]);
        Assert.Empty(prepared.Errors);

        var (app, errors) = CordOps.Apply(draft, prepared.Ops);

        Assert.Empty(errors);
        Assert.Equal(4, app.UseList.Count);
        Assert.Equal(["crm_eu", "crm_us"], app.UseList.Where(u => u.Integration is not null).Select(u => u.As!));
    }
}
