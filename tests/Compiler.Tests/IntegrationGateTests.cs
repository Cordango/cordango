// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.Integrations;

namespace Cordango.Compiler.Tests;

public class IntegrationGateTests
{
    private const string Uses = """[{ "integration": "hubspot.crm@1", "as": "crm", "why": "contacts live in HubSpot" }]""";

    private static JsonObject Def(string blocks, string uses = Uses) => (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"sales_dashboard","name":"Sales dashboard","version":"1.0.0",
          "uses": {{uses}},
          "entities":[
            {"key":"note","label":"Note","labelPlural":"Notes","displayField":"title","fields":[
              {"key":"title","label":"Title","type":"text"}
            ]}
          ],
          "pages":[{"key":"overview","label":"Overview","blocks":{{blocks}}}]
        }
        """)!;

    private const string ContactTable = """
        [{"kind":"table","label":"Contacts",
          "source":{"integration":"crm","entity":"contact","sort":[{"field":"updated_at","direction":"desc"}]},
          "fields":["first_name","last_name","email","lifecycle_stage"]}]
    """;

    private static List<string> Errors(string blocks, string uses = Uses, KnownIntegrations? known = null) =>
        Gate.SemanticErrors(Def(blocks, uses), null, known);

    private static string Stat(string aggregate, string filters = "[]") => $$$"""
        [{"kind":"stat","label":"Contacts","source":{"integration":"crm","entity":"contact","filters":{{{filters}}},"aggregate":{{{aggregate}}}}}]
    """;

    [Fact]
    public void A_read_only_table_over_a_declared_integration_is_valid() =>
        Assert.Empty(Errors(ContactTable));

    [Fact]
    public void The_whole_gate_accepts_it_including_the_schema() =>
        Assert.DoesNotContain(Gate.Validate(Def(ContactTable)), e => !e.StartsWith("DESIGN", StringComparison.Ordinal));

    [Fact]
    public void An_entity_the_contract_does_not_have_is_refused_with_what_it_has() =>
        Assert.Contains(Errors(ContactTable.Replace("\"entity\":\"contact\"", "\"entity\":\"contacts\"", StringComparison.Ordinal)),
            e => e.Contains("hubspot.crm has: contact"));

    [Fact]
    public void A_filter_the_contract_does_not_declare_is_refused() =>
        Assert.Contains(Errors(Stat("""{"op":"count"}""", """[{"field":"phone","operator":"eq","value":"1"}]""")),
            e => e.Contains("hubspot.crm.contact is not filterable by 'phone'"));

    [Fact]
    public void A_declared_filter_with_an_undeclared_operator_is_refused() =>
        Assert.Contains(Errors(Stat("""{"op":"count"}""", """[{"field":"email","operator":"contains","value":"@acme"}]""")),
            e => e.Contains("filters 'email' with eq, not 'contains'"));

    [Fact]
    public void A_sort_the_contract_does_not_declare_is_refused() =>
        Assert.Contains(Errors(ContactTable.Replace("updated_at", "last_name", StringComparison.Ordinal)),
            e => e.Contains("is not sortable by 'last_name'"));

    [Fact]
    public void An_exact_count_the_contract_declares_is_valid() =>
        Assert.Empty(Errors(Stat("""{"op":"count"}""", """[{"field":"lifecycle_stage","operator":"eq","value":"customer"}]""")));

    [Fact]
    public void A_sum_the_provider_cannot_compute_exactly_is_refused() =>
        Assert.Contains(Errors(Stat("""{"op":"sum","field":"first_name"}""")),
            e => e.Contains("hubspot.crm.contact has no sum of 'first_name'"));

    [Fact]
    public void A_grouped_figure_is_refused() =>
        Assert.Contains(Errors(Stat("""{"op":"count","groupBy":"lifecycle_stage"}""")),
            e => e.Contains("offers no grouped figure"));

    [Fact]
    public void An_alias_that_was_never_declared_is_refused() =>
        Assert.Contains(Errors(ContactTable, "[]"), e => e.Contains("source integration 'crm' is not declared"));

    [Fact]
    public void Two_connections_of_one_contract_are_two_names() =>
        Assert.Empty(Errors(ContactTable,
            """[{ "integration": "hubspot.crm@1", "as": "crm" }, { "integration": "hubspot.crm@1", "as": "crm_us" }]"""));

    [Fact]
    public void One_name_for_two_integrations_is_refused() =>
        Assert.Contains(Errors(ContactTable,
            """[{ "integration": "hubspot.crm@1", "as": "crm" }, { "integration": "hubspot.crm@1", "as": "crm" }]"""),
            e => e.Contains("gives the name 'crm' to two integrations"));

    [Fact]
    public void A_contract_outside_a_closed_roster_is_refused() =>
        Assert.Contains(Errors(ContactTable, """[{ "integration": "acme.erp@1", "as": "crm" }]""", KnownIntegrations.Of([])),
            e => e.Contains("names integration 'acme.erp@1', which is not a known contract"));

    [Fact]
    public void Without_a_roster_an_unknown_contract_is_left_unjudged() =>
        Assert.Empty(Errors(ContactTable, """[{ "integration": "acme.erp@1", "as": "crm" }]"""));

    [Fact]
    public void A_table_over_an_integration_is_read_only() =>
        Assert.Contains(Errors(ContactTable.Replace("\"label\":\"Contacts\",", "\"label\":\"Contacts\",\"inlineEdit\":true,", StringComparison.Ordinal)),
            e => e.Contains("'inlineEdit' — a table over an integration is read-only"));

    [Fact]
    public void Searching_the_loaded_page_is_refused() =>
        Assert.Contains(Errors(ContactTable.Replace("\"label\":\"Contacts\",", "\"label\":\"Contacts\",\"filterBar\":{\"search\":[\"email\"]},", StringComparison.Ordinal)),
            e => e.Contains("'filterBar' works on the rows that loaded"));

    [Fact]
    public void A_column_the_contract_does_not_have_is_refused() =>
        Assert.Contains(Errors(ContactTable.Replace("\"lifecycle_stage\"]", "\"stage\"]", StringComparison.Ordinal)),
            e => e.Contains("table column 'stage' is not a field of crm.contact"));

    [Fact]
    public void A_board_over_an_integration_is_not_available_yet() =>
        Assert.Contains(Errors("""
            [{"kind":"board","label":"Pipeline","source":{"integration":"crm","entity":"contact"},"groupBy":"lifecycle_stage"}]
            """), e => e.Contains("A board is not available over an integration yet"));

    [Fact]
    public void A_source_is_one_app_or_one_integration() =>
        Assert.Contains(Errors("""
            [{"kind":"repeat","as":"c","source":{"integration":"crm","app":"crm_app","entity":"contact"},"blocks":[{"kind":"field","field":"email"}]}]
            """), e => e.Contains("gives both 'app' and 'integration'"));

    [Fact]
    public void A_repeat_reads_plain_fields_of_the_external_row() =>
        Assert.Empty(Errors("""
            [{"kind":"repeat","as":"c","source":{"integration":"crm","entity":"contact"},"blocks":[{"kind":"field","field":"email"}]}]
            """));

    [Fact]
    public void A_repeat_cannot_hop_through_an_external_row() =>
        Assert.Contains(Errors("""
            [{"kind":"repeat","as":"c","source":{"integration":"crm","entity":"contact"},"blocks":[{"kind":"field","field":"company.name"}]}]
            """), e => e.Contains("hops a relation inside 'crm'"));

    [Fact]
    public void A_name_without_an_integration_is_refused() =>
        Assert.Contains(Errors("[]", """[{ "app": "core_organizations", "as": "orgs" }]"""),
            e => e.Contains("gives 'as: orgs' without 'integration'"));

    [Fact]
    public void An_integration_without_a_name_is_refused_by_the_schema() =>
        Assert.Contains(Gate.Validate(Def("[]", """[{ "integration": "hubspot.crm@1" }]""")),
            e => e.StartsWith("STRUCTURAL", StringComparison.Ordinal) && e.Contains("as"));
}
