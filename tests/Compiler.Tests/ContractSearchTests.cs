// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;

namespace Cordango.Compiler.Tests;

public class ContractSearchTests
{
    private static JsonObject Contract(string key, string purpose = "", string entities = "",
        string events = "", string actions = "") => (JsonObject)JsonNode.Parse($$"""
    {
      "contractVersion":"1.0","kind":"app-contract",
      "identity":{"key":"{{key}}","name":"{{key}}"},
      "purpose":{"summary":"{{purpose}}"},
      "entities":[{{entities}}],
      "dependencies":[],
      "events":[{{events}}],
      "actions":[{{actions}}],
      "rules":[]
    }
    """)!;

    private static readonly JsonObject Vendors = Contract("vendor_management",
        purpose: "Onboards suppliers and keeps their compliance documents current",
        entities: """{"key":"vendor","label":"Vendor"},{"key":"document","label":"Document"}""",
        events: """{"name":"vendor.approved","type":"process.state_entered"}""",
        actions: """{"id":"vendor.approve","label":"Approve"}""");

    private static readonly JsonObject Crm = Contract("sales_crm",
        purpose: "Moves deals through the pipeline",
        entities: """{"key":"deal","label":"Deal"}""",
        events: """{"name":"deal.won","type":"process.state_entered"}""");

    private static readonly JsonObject Helpdesk = Contract("helpdesk",
        purpose: "Answers customer tickets",
        entities: """{"key":"ticket","label":"Ticket"}""");

    private static List<JsonObject> All => [Vendors, Crm, Helpdesk];

    private static string Key(ContractMatch m) => m.Contract["identity"]!["key"]!.GetValue<string>();

    [Fact]
    public void A_word_in_the_purpose_finds_an_app_whose_entities_are_named_differently()
    {
        var found = ContractSearch.Rank(All, "supplier");

        Assert.Equal("vendor_management", Key(found[0]));
    }

    [Fact]
    public void An_entity_key_outranks_a_word_in_prose()
    {
        var found = ContractSearch.Rank([Crm, Vendors], "deal");

        Assert.Equal("sales_crm", Key(found[0]));
    }

    [Fact]
    public void An_event_is_findable_by_the_verb_alone()
    {
        var found = ContractSearch.Rank(All, "approved");

        Assert.Equal("vendor_management", Key(found[0]));
        Assert.Contains(found[0].Matched, m => m.StartsWith("event:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_match_says_which_part_matched()
    {
        var found = ContractSearch.Rank(All, "ticket");

        Assert.Contains("entity:ticket", found[0].Matched);
    }

    [Fact]
    public void Nothing_relevant_comes_back_empty_rather_than_ranked_badly()
    {
        Assert.Empty(ContractSearch.Rank(All, "photosynthesis"));
    }

    [Fact]
    public void No_query_lists_what_there_is()
    {
        Assert.Equal(3, ContractSearch.Rank(All, null, limit: 10).Count);
    }

    [Fact]
    public void The_limit_is_honoured()
    {
        Assert.Single(ContractSearch.Rank(All, "vendor deal ticket", limit: 1));
    }

    [Fact]
    public void A_section_narrows_what_is_searched()
    {
        Assert.Empty(ContractSearch.Rank(All, "ticket", section: "events"));
        Assert.NotEmpty(ContractSearch.Rank(All, "ticket", section: "entities"));
    }

    [Fact]
    public void Ranking_the_same_question_twice_gives_the_same_order()
    {
        var once = ContractSearch.Rank(All, "vendor").Select(Key);
        var twice = ContractSearch.Rank([Helpdesk, Crm, Vendors], "vendor").Select(Key);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void A_question_with_nothing_searchable_in_it_lists_what_there_is()
    {
        Assert.Equal(3, ContractSearch.Rank(All, "a").Count);
    }
}
