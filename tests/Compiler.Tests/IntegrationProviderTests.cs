// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Integrations;

namespace Cordango.Compiler.Tests;

public class IntegrationProviderTests
{
    private static readonly IntegrationContract Acme = ContractCompiler.Compile(JsonNode.Parse("""
        {
          "kind": "IntegrationContract", "name": "acme.crm", "version": 1,
          "entities": { "company": {
            "displayField": "name",
            "fields": {
              "id": { "type": "text", "readonly": true },
              "name": { "type": "text", "required": true },
              "website": { "type": "url" },
              "industry": { "type": "select", "options": ["software", "retail", "other"] }
            },
            "capabilities": ["list", "get"],
            "query": { "filter": { "industry": ["eq"] }, "sort": ["name"], "aggregate": { "count": true } }
          } }
        }
        """)).Contract!;

    private static KnownIntegrations Roster => KnownIntegrations.Of([Acme]);

    private const string Provider = """
        {
          "kind": "IntegrationProvider", "name": "acme", "version": 1,
          "implements": ["acme.crm@1"],
          "config": { "region": { "label": "Region", "type": "select", "options": ["eu", "us"] } },
          "http": { "baseUrl": "https://api-{{config.region}}.acme.example/v3" },
          "auth": { "type": "api_key", "scope": "workspace", "placement": { "header": "X-Api-Key" } },
          "test": { "path": "/companies", "query": { "limit": "1" }, "expect": "$.results" },
          "entities": { "company": {
            "operations": {
              "list": {
                "path": "/companies",
                "paging": { "style": "cursor", "param": "after", "sizeParam": "limit" },
                "response": { "items": "$.results", "nextCursor": "$.next", "total": "$.total" },
                "filters": { "fields": { "industry": { "name": "industry_code", "values": { "software": "SW", "retail": "RT", "other": "OT" } } },
                             "query": "{{filter.name}}" },
                "sort": { "fields": { "name": "name" }, "query": { "param": "sort", "asc": "{{sort.name}}", "desc": "-{{sort.name}}" } }
              },
              "get": { "path": "/companies/{{id}}", "notFound": [404] }
            },
            "aggregate": { "count": { "via": "list" } },
            "mapping": { "fromProvider": {
              "id": "$.id",
              "name": "$.name",
              "industry": { "from": "$.industry_code", "transform": { "enum": { "SW": "software", "RT": "retail" }, "otherwise": "other" } }
            } }
          } }
        }
        """;

    private static AdapterOutcome Compile(string json, bool catalog = false) =>
        AdapterCompiler.Compile(JsonNode.Parse(json), Roster, catalog);

    private static IReadOnlyList<string> Errors(string from, string to) =>
        Compile(Provider.Replace(from, to, StringComparison.Ordinal)).Errors;

    [Fact]
    public void A_provider_that_implements_its_contract_compiles_to_a_plan()
    {
        var outcome = Compile(Provider);

        Assert.True(outcome.Ok, string.Join("\n", outcome.Errors));
        var plan = outcome.Plan!;
        Assert.Equal("integration-plan", (string)plan["kind"]!);
        Assert.Equal(Acme.Hash, (string)plan["contracts"]!["acme.crm@1"]!);
        var entity = plan["entities"]![0]!;
        Assert.Equal("list", (string)entity["queryOperation"]!);
        Assert.Equal(100, (int)entity["operations"]!["list"]!["paging"]!["size"]!);
        Assert.Equal("$", (string)entity["operations"]!["get"]!["response"]!["item"]!);
        Assert.Equal(outcome.Hash, Compile(Provider).Hash);
    }

    [Fact]
    public void An_unmapped_optional_field_is_noted_as_unavailable()
    {
        var outcome = Compile(Provider);

        Assert.Contains(outcome.Notes, n => n.Contains("'website' is not mapped, so it is unavailable"));
    }

    [Fact]
    public void A_secret_is_never_a_template_variable() =>
        Assert.Contains(Errors("\"query\": { \"limit\": \"1\" }", "\"query\": { \"limit\": \"1\", \"key\": \"{{auth.token}}\" }"),
            e => e.Contains("'{{auth.token}}' is not a variable") && e.Contains("A credential is never a template variable"));

    [Fact]
    public void An_undeclared_config_value_is_refused() =>
        Assert.Contains(Errors("\"/companies/{{id}}\"", "\"/{{config.tenant}}/companies/{{id}}\""),
            e => e.Contains("names config 'tenant', which is not declared"));

    [Fact]
    public void Plain_http_is_refused_when_a_credential_is_sent() =>
        Assert.Contains(Errors("https://api-", "http://api-"), e => e.Contains("plain http and this provider sends a credential"));

    [Fact]
    public void A_free_text_host_variable_is_refused()
    {
        var errors = Errors("\"type\": \"select\", \"options\": [\"eu\", \"us\"]", "\"type\": \"text\"");

        Assert.Contains(errors, e => e.Contains("sits in the host, so it must be a select or a text with a 'pattern'"));
    }

    [Fact]
    public void A_host_variable_with_a_safe_pattern_is_accepted() =>
        Assert.True(Compile(Provider.Replace("\"type\": \"select\", \"options\": [\"eu\", \"us\"]",
            "\"type\": \"text\", \"pattern\": \"^[a-z]{2}$\"", StringComparison.Ordinal)).Ok);

    [Fact]
    public void A_credential_header_is_refused() =>
        Assert.Contains(Errors("\"http\": { \"baseUrl\": \"https://api-{{config.region}}.acme.example/v3\" }",
                               "\"http\": { \"baseUrl\": \"https://api-{{config.region}}.acme.example/v3\", \"headers\": { \"Authorization\": \"Bearer x\" } }"),
            e => e.Contains("carries a credential"));

    [Fact]
    public void An_unknown_contract_is_refused() =>
        Assert.Contains(Errors("\"implements\": [\"acme.crm@1\"]", "\"implements\": [\"acme.crm@2\"]"),
            e => e.Contains("implements 'acme.crm@2', which is not a known contract"));

    [Fact]
    public void A_capability_without_its_operation_is_refused() =>
        Assert.Contains(Errors("\"get\": {", "\"search\": { \"method\": \"POST\", \"response\": { \"items\": \"$.r\" },"),
            e => e.Contains("can be 'get', and this entry has no 'get' operation"));

    [Fact]
    public void A_declared_filter_the_operation_cannot_apply_is_refused() =>
        Assert.Contains(Errors("\"industry\": { \"name\": \"industry_code\"", "\"website\": { \"name\": \"industry_code\""),
            e => e.Contains("filter 'industry': the contract says it is filterable, and the operation does not say how"));

    [Fact]
    public void A_filter_on_a_translated_field_must_translate_back() =>
        Assert.Contains(Errors(", \"values\": { \"software\": \"SW\", \"retail\": \"RT\", \"other\": \"OT\" }", ""),
            e => e.Contains("must translate them back with 'values'"));

    [Fact]
    public void A_required_field_must_be_mapped() =>
        Assert.Contains(Errors("\"name\": \"$.name\",", ""), e => e.Contains("'name' is required by the contract and is not mapped"));

    [Fact]
    public void A_declared_count_needs_an_operation_that_reports_a_total() =>
        Assert.Contains(Errors(", \"total\": \"$.total\"", ""), e => e.Contains("needs that operation's response.total"));

    [Fact]
    public void A_get_request_has_no_body() =>
        Assert.Contains(Errors("\"get\": { \"path\": \"/companies/{{id}}\"", "\"get\": { \"path\": \"/companies/{{id}}\", \"body\": { \"a\": 1 }"),
            e => e.Contains("a GET request has no body"));

    [Fact]
    public void Cursor_paging_needs_to_know_where_the_next_cursor_is() =>
        Assert.Contains(Errors(", \"nextCursor\": \"$.next\"", ""), e => e.Contains("cursor paging needs response.nextCursor"));

    [Fact]
    public void A_path_that_leaves_the_base_address_is_refused() =>
        Assert.Contains(Errors("\"/companies/{{id}}\"", "\"/companies/../admin/{{id}}\""), e => e.Contains("must stay below the base address"));

    [Fact]
    public void The_platform_oauth_client_is_reserved_for_the_catalog()
    {
        var oauth = Provider.Replace(
            "\"auth\": { \"type\": \"api_key\", \"scope\": \"workspace\", \"placement\": { \"header\": \"X-Api-Key\" } }",
            "\"auth\": { \"type\": \"oauth2\", \"scope\": \"per_user\", \"authorizeUrl\": \"https://acme.example/oauth/authorize\", \"tokenUrl\": \"https://acme.example/oauth/token\", \"client\": \"platform\" }",
            StringComparison.Ordinal);

        Assert.Contains(Compile(oauth).Errors, e => e.Contains("client 'platform' is reserved for providers in the catalog"));
        Assert.True(Compile(oauth, catalog: true).Ok);
    }

    [Fact]
    public void An_oauth_endpoint_must_be_a_fixed_https_address()
    {
        var oauth = Provider.Replace(
            "\"auth\": { \"type\": \"api_key\", \"scope\": \"workspace\", \"placement\": { \"header\": \"X-Api-Key\" } }",
            "\"auth\": { \"type\": \"oauth2\", \"scope\": \"workspace\", \"authorizeUrl\": \"https://acme.example/authorize\", \"tokenUrl\": \"https://{{config.region}}.acme.example/token\" }",
            StringComparison.Ordinal);

        Assert.Contains(Compile(oauth).Errors, e => e.Contains("'tokenUrl' must be a fixed https address"));
    }

    [Fact]
    public void An_api_key_needs_a_placement() =>
        Assert.Contains(Errors(", \"placement\": { \"header\": \"X-Api-Key\" }", ""), e => e.Contains("an api_key needs 'placement'"));
}
