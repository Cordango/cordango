// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class AppContractTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static JsonObject Definition(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(
            Path.Combine(Corpus.RepoRoot(), "tests", "corpus", "reference", name)))!;

    private static JsonObject Contract(string name = "sales-crm.appdef.json")
    {
        var def = Definition(name);
        return AppContract.Build(def, AppCompiler.Compile(def, "app1", At));
    }

    private static JsonArray Arr(JsonObject o, string key) => (JsonArray)o[key]!;

    private static JsonObject? ById(JsonObject contract, string section, string id) =>
        Arr(contract, section).OfType<JsonObject>()
            .FirstOrDefault(x => (x["id"] ?? x["name"])?.GetValue<string>() == id);

    [Fact]
    public void The_contract_states_its_own_version_and_kind()
    {
        var c = Contract();

        Assert.Equal("1.0", c["contractVersion"]!.GetValue<string>());
        Assert.Equal("app-contract", c["kind"]!.GetValue<string>());
    }

    [Fact]
    public void The_contract_carries_no_host_facts()
    {
        var text = Contract().ToJsonString();

        Assert.DoesNotContain("/api/", text);
        Assert.DoesNotContain("app1", text);
        Assert.DoesNotContain("handle", text);
    }

    [Fact]
    public void The_contract_has_exactly_the_sections_version_one_promises()
    {
        Assert.Equal(
            ["contractVersion", "kind", "identity", "purpose", "entities", "dependencies",
             "events", "actions", "rules"],
            Contract().Select(p => p.Key));
    }

    [Fact]
    public void Identity_names_the_definition_it_was_built_from()
    {
        var identity = (JsonObject)Contract()["identity"]!;

        Assert.Equal("sales_crm", identity["key"]!.GetValue<string>());
        Assert.NotNull(identity["definitionHash"]);
    }

    [Fact]
    public void A_state_event_is_caused_by_the_action_that_enters_it_and_emitted_by_nothing()
    {
        var won = ById(Contract(), "events", "deal.won")!;

        Assert.Equal("process.state_entered", won["type"]!.GetValue<string>());
        Assert.Equal("deal_flow", won["process"]!.GetValue<string>());
        Assert.Equal(["deal.mark_won"], Arr(won, "causedByActions").Select(x => x!.GetValue<string>()));
        Assert.Null(won["emittedBy"]);
    }

    [Fact]
    public void Every_entity_announces_the_three_writes_without_anyone_authoring_them()
    {
        var names = Arr(Contract(), "events").Select(e => e!["name"]!.GetValue<string>()).ToList();

        Assert.Contains("activity.created", names);
        Assert.Contains("activity.updated", names);
        Assert.Contains("activity.deleted", names);
    }

    [Fact]
    public void An_action_bound_to_a_transition_says_what_it_moves_and_what_that_causes()
    {
        var won = ById(Contract(), "actions", "deal.mark_won")!;
        var transition = (JsonObject)won["transition"]!;

        Assert.Equal("win", transition["key"]!.GetValue<string>());
        Assert.Equal(["open"], Arr(transition, "from").Select(x => x!.GetValue<string>()));
        Assert.Equal("won", transition["to"]!.GetValue<string>());
        Assert.Equal(["deal.won", "deal.updated"], Arr(won, "causes").Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void A_required_input_becomes_a_rule_with_an_addressable_name()
    {
        var c = Contract();
        var won = ById(c, "actions", "deal.mark_won")!;

        Assert.Contains("deal.mark_won.requires_won_reason",
            Arr(won, "requires").Select(x => x!.GetValue<string>()));

        var rule = ById(c, "rules", "deal.mark_won.requires_won_reason")!;
        Assert.Equal("required", rule["kind"]!.GetValue<string>());
        Assert.Equal("stop", rule["effect"]!.GetValue<string>());
        Assert.Equal(["won_reason"],
            Arr((JsonObject)rule["assertion"]!, "fields").Select(x => x!.GetValue<string>()));
        Assert.Equal(["command:mark_won"], Arr(rule, "on").Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void A_transition_becomes_a_state_rule_that_says_where_it_may_run_from()
    {
        var rule = ById(Contract(), "rules", "deal.win.from")!;

        Assert.Equal("state", rule["kind"]!.GetValue<string>());
        Assert.Equal(["open"], Arr(rule, "from").Select(x => x!.GetValue<string>()));
        Assert.Equal("won", rule["to"]!.GetValue<string>());
    }

    [Fact]
    public void Dependencies_report_what_the_app_points_at_and_how_that_is_known()
    {
        var deps = Arr(Contract(), "dependencies").OfType<JsonObject>().ToList();

        var orgs = deps.Single(d => d["app"]!.GetValue<string>() == "core_organizations");
        Assert.Equal("reference", orgs["source"]!.GetValue<string>());
        Assert.Contains("organization", Arr(orgs, "entities").Select(x => x!.GetValue<string>()));
        Assert.Contains("platform", deps.Select(d => d["app"]!.GetValue<string>()));
    }

    [Fact]
    public void A_field_says_what_a_caller_may_put_in_it()
    {
        var deal = Arr(Contract(), "entities").OfType<JsonObject>()
            .Single(e => e["key"]!.GetValue<string>() == "deal");
        var stage = Arr(deal, "fields").OfType<JsonObject>()
            .Single(f => f["key"]!.GetValue<string>() == "stage");

        Assert.Equal("select", stage["type"]!.GetValue<string>());
        Assert.Contains("lead_in", Arr(stage, "options").OfType<JsonObject>()
            .Select(o => o["value"]!.GetValue<string>()));
    }

    [Fact]
    public void System_fields_are_the_runtimes_and_stay_out_of_the_contract()
    {
        var deal = Arr(Contract(), "entities").OfType<JsonObject>()
            .Single(e => e["key"]!.GetValue<string>() == "deal");

        Assert.DoesNotContain("company_id", Arr(deal, "fields").OfType<JsonObject>()
            .Select(f => f["key"]!.GetValue<string>()));
    }

    [Fact]
    public void A_transition_nobody_bound_a_command_to_still_becomes_an_action()
    {
        var def = (JsonObject)JsonNode.Parse("""
        {
          "schemaVersion":"2.0","key":"tasks","name":"Tasks","version":"1.0.0",
          "entities":[
            {"key":"task","label":"Task","labelPlural":"Tasks","displayField":"title","fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"status","label":"Status","type":"select","role":"status","options":[
                {"value":"open","label":"Open"},{"value":"done","label":"Done"}]}
            ]}
          ],
          "processes":[
            {"key":"flow","entity":"task","stateField":"status","initialState":"open",
             "states":[{"key":"open","label":"Open"},{"key":"done","label":"Done","terminal":true}],
             "transitions":[{"key":"complete","label":"Complete","from":["open"],"to":"done"}]}
          ]
        }
        """)!;

        var contract = AppContract.Build(def, AppCompiler.Compile(def, "app1", At));
        var action = Assert.Single(Arr(contract, "actions").OfType<JsonObject>());

        Assert.True(action["synthesized"]!.GetValue<bool>());
        Assert.Equal("done", ((JsonObject)action["transition"]!)["to"]!.GetValue<string>());
        Assert.Equal(["task.done", "task.updated"], Arr(action, "causes").Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void An_app_that_announces_nothing_still_lists_its_writes_and_its_states()
    {
        var events = Arr(Contract(), "events").OfType<JsonObject>().ToList();

        Assert.DoesNotContain(events, e => e["type"]!.GetValue<string>() == "command.emitted");
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "process.state_entered");
        Assert.Contains(events, e => e["type"]!.GetValue<string>() == "record.created");
    }

    [Fact]
    public void An_announced_name_is_a_command_event_and_names_what_publishes_it()
    {
        var def = Definition("sales-crm.appdef.json");
        ((JsonObject)((JsonArray)def["commands"]!)[0]!)["emits"] = new JsonArray("deal.closed");

        var contract = AppContract.Build(def, AppCompiler.Compile(def, "app1", At));
        var closed = ById(contract, "events", "deal.closed")!;

        Assert.Equal("command.emitted", closed["type"]!.GetValue<string>());
        Assert.Equal(["deal.mark_won"], Arr(closed, "emittedBy").Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Compiling_the_same_definition_twice_produces_the_same_bytes()
    {
        Assert.Equal(ContractWriter.Text(Contract()), ContractWriter.Text(Contract()));
    }

    [Fact]
    public void Every_reference_app_compiles_to_a_contract()
    {
        foreach (var path in Corpus.SemanticPaths())
        {
            var def = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            var contract = AppContract.Build(def, AppCompiler.Compile(def, "app", At));

            Assert.NotEmpty(Arr(contract, "events"));
            Assert.NotNull(ContractWriter.HashOf(ContractWriter.Seal(contract)));
        }
    }

    [Fact]
    public void Sealing_fills_the_hash_and_sealing_again_does_not_change_it()
    {
        var once = ContractWriter.Seal(Contract());
        var twice = ContractWriter.Seal(once);

        Assert.NotNull(ContractWriter.HashOf(once));
        Assert.Equal(ContractWriter.HashOf(once), ContractWriter.HashOf(twice));
    }

    [Fact]
    public void A_contract_that_says_something_different_hashes_differently()
    {
        var mine = Contract();
        var theirs = Contract();
        ((JsonObject)theirs["identity"]!)["version"] = "9.9.9";

        Assert.NotEqual(ContractWriter.HashOf(ContractWriter.Seal(mine)),
            ContractWriter.HashOf(ContractWriter.Seal(theirs)));
    }

    [Fact]
    public void The_bytes_end_in_exactly_one_newline()
    {
        var text = ContractWriter.Text(Contract());

        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public void The_bytes_do_not_depend_on_the_machine_that_wrote_them()
    {
        var text = ContractWriter.Text(Contract());

        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void A_provisional_contract_admits_it_has_no_definition_to_name()
    {
        var manifest = AppCompiler.Compile(Definition("sales-crm.appdef.json"), "app1", At);

        var contract = AppContract.FromManifest(manifest);

        Assert.Null(ContractWriter.DefinitionHashOf(contract));
        Assert.NotEmpty(Arr(contract, "events"));
    }

    [Fact]
    public void Reading_back_something_that_is_not_a_contract_yields_nothing()
    {
        Assert.Null(ContractWriter.Read("{\"kind\":\"app-manifest\"}"));
        Assert.Null(ContractWriter.Read("{ truncated"));
        Assert.Null(ContractWriter.Read(""));
        Assert.NotNull(ContractWriter.Read(ContractWriter.Text(Contract())));
    }
}
