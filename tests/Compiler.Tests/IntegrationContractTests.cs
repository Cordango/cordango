// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Integrations;

namespace Cordango.Compiler.Tests;

public class IntegrationContractTests
{
    private static JsonObject Contract(string entity) => (JsonObject)JsonNode.Parse($$"""
        {
          "kind": "IntegrationContract", "name": "acme.crm", "version": 1,
          "entities": { "company": {{entity}} }
        }
        """)!;

    private const string Company = """
        {
          "displayField": "name",
          "fields": {
            "id": { "type": "text", "readonly": true },
            "name": { "type": "text", "required": true },
            "employees": { "type": "integer" },
            "industry": { "type": "select", "options": ["software", "retail"] }
          },
          "capabilities": ["list", "get"],
          "query": {
            "filter": { "industry": ["eq", "in"], "employees": ["gte"] },
            "sort": ["name"],
            "aggregate": { "count": true, "sum": ["employees"] }
          }
        }
        """;

    private static IReadOnlyList<string> Errors(string entity) => ContractCompiler.Compile(Contract(entity)).Errors;

    private static string With(string from, string to) => Company.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void The_catalog_compiles_without_a_problem()
    {
        Assert.Empty(IntegrationCatalog.Problems);
        Assert.Contains(IntegrationCatalog.Contracts, c => c.Ref.ToString() == "hubspot.crm@1");
    }

    [Fact]
    public void Every_catalog_provider_implements_its_contracts()
    {
        Assert.NotEmpty(IntegrationCatalog.Providers);
        foreach (var provider in IntegrationCatalog.Providers)
        {
            var outcome = AdapterCompiler.Compile(provider, KnownIntegrations.Unknown, catalog: true);
            Assert.True(outcome.Ok, string.Join("\n", outcome.Errors));
        }
    }

    [Fact]
    public void A_contract_compiles_to_its_entities_with_defaults_written_out()
    {
        var outcome = ContractCompiler.Compile(Contract(Company));

        Assert.True(outcome.Ok, string.Join("\n", outcome.Errors));
        var entity = outcome.Contract!.Entity("company")!;
        Assert.Equal("Company", entity.Label);
        Assert.Equal("Industry", entity.Field("industry")!.Label);
        Assert.True(entity.Aggregates.Allows("count", null));
        Assert.True(entity.Aggregates.Allows("sum", "employees"));
        Assert.False(entity.Aggregates.Allows("avg", "employees"));
        Assert.Equal("acme.crm@1", (string)outcome.Contract.Compiled["ref"]!);
    }

    [Fact]
    public void The_same_source_compiles_to_the_same_bytes_in_any_key_order()
    {
        var reordered = (JsonObject)JsonNode.Parse("""
            {
              "entities": { "company": {
                "query": { "sort": ["name"], "aggregate": { "sum": ["employees"], "count": true },
                           "filter": { "employees": ["gte"], "industry": ["in", "eq"] } },
                "capabilities": ["get", "list"],
                "fields": {
                  "id": { "readonly": true, "type": "text" },
                  "name": { "required": true, "type": "text" },
                  "employees": { "type": "integer" },
                  "industry": { "options": ["software", "retail"], "type": "select" }
                },
                "displayField": "name"
              } },
              "version": 1, "name": "acme.crm", "kind": "IntegrationContract"
            }
            """)!;

        var first = ContractCompiler.Compile(Contract(Company)).Contract!;
        var second = ContractCompiler.Compile(reordered).Contract!;

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Compiled.ToJsonString(), second.Compiled.ToJsonString());
    }

    [Fact]
    public void An_entity_without_an_id_is_refused() =>
        Assert.Contains(Errors(With("\"id\": { \"type\": \"text\", \"readonly\": true },", "")),
            e => e.Contains("needs an 'id' field"));

    [Fact]
    public void Writes_are_refused_until_they_exist() =>
        Assert.Contains(Errors(With("[\"list\", \"get\"]", "[\"list\", \"get\", \"update\"]")),
            e => e.Contains("capability 'update'") && e.Contains("read-only for now"));

    [Fact]
    public void A_range_operator_on_text_is_refused() =>
        Assert.Contains(Errors(With("\"filter\": { \"industry\"", "\"filter\": { \"name\": [\"gt\"], \"industry\"")),
            e => e.Contains("'gt' compares numbers and dates, and this field is text"));

    [Fact]
    public void A_sum_over_text_is_refused() =>
        Assert.Contains(Errors(With("\"sum\": [\"employees\"]", "\"sum\": [\"name\"]")),
            e => e.Contains("aggregate sum over 'name' needs a number"));

    [Fact]
    public void A_select_without_options_is_refused() =>
        Assert.Contains(Errors(With(", \"options\": [\"software\", \"retail\"]", "")),
            e => e.Contains("a select field needs 'options'"));

    [Fact]
    public void A_field_key_with_a_double_underscore_is_reserved_for_providers() =>
        Assert.Contains(Errors(With("\"employees\": { \"type\": \"integer\" }", "\"hubspot__employees\": { \"type\": \"integer\" }")),
            e => e.Contains("'__' is reserved for provider-specific fields"));

    [Fact]
    public void A_display_field_that_is_not_a_field_is_refused() =>
        Assert.Contains(Errors(With("\"displayField\": \"name\"", "\"displayField\": \"title\"")),
            e => e.Contains("displayField 'title' is not one of its fields"));

    [Fact]
    public void A_name_without_a_namespace_is_refused_by_the_schema()
    {
        var doc = Contract(Company);
        doc["name"] = "crm";

        Assert.Contains(ContractCompiler.Compile(doc).Errors, e => e.StartsWith("STRUCTURAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reference_reads_name_and_major()
    {
        Assert.Equal(new IntegrationRef("hubspot.crm", 1), IntegrationRef.TryParse("hubspot.crm@1"));
        Assert.Null(IntegrationRef.TryParse("hubspot.crm"));
        Assert.Null(IntegrationRef.TryParse("hubspot.crm@0"));
    }

    [Fact]
    public void A_closed_roster_holds_the_catalog_and_the_workspace()
    {
        var own = ContractCompiler.Compile(Contract(Company)).Contract!;
        var roster = KnownIntegrations.Of([own]);

        Assert.True(roster.Complete);
        Assert.NotNull(roster.Find("acme.crm@1"));
        Assert.NotNull(roster.Find("hubspot.crm@1"));
        Assert.Null(roster.Find("acme.crm@2"));
        Assert.False(KnownIntegrations.Unknown.Complete);
        Assert.NotNull(KnownIntegrations.Unknown.Find("hubspot.crm@1"));
    }
}
