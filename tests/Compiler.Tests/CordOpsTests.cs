// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

public class CordOpsTests(ITestOutputHelper output)
{
    private static JsonNode Ops(string json) => JsonNode.Parse(json)!;

    private static (CordApp App, IReadOnlyList<CordError> Errors) Apply(CordApp draft, string json)
    {
        var prepared = CordTransaction.Prepare(draft, Ops(json)["ops"]);
        return (prepared.Next, prepared.Errors);
    }

    [Fact]
    public void One_call_can_establish_an_entire_domain()
    {
        var (app, errors) = Apply(new CordApp(Key: "budget"), """
        {"ops":[
          {"op":"upsert_entity","entity":
            {"key":"scenario","label":"Scenario","fields":[
              {"key":"name","label":"Name","type":"text","required":true},
              {"key":"starting_cash","label":"Starting cash","type":"money"}]}},
          {"op":"upsert_entity","entity":
            {"key":"hiring_line","label":"Hire","fields":[
              {"key":"scenario","label":"Scenario","type":"reference","target":"scenario","required":true},
              {"key":"role","label":"Role","type":"text"},
              {"key":"gross_salary","label":"Gross salary","type":"money"},
              {"key":"employer_cost_rate","label":"Employer cost %","type":"decimal"},
              {"key":"monthly_cost","label":"Loaded monthly cost","type":"money",
               "expr":"gross_salary * (1 + employer_cost_rate / 100) / 12"}]}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["scenario", "hiring_line"], app.EntityList.Select(e => e.Key));

        var loaded = app.EntityList[1].FieldList.Single(f => f.Key == "monthly_cost");
        Assert.IsType<CordExpr>(loaded.Calc);
    }

    [Fact]
    public void An_aggregate_lowers_into_machinery_the_author_never_mentioned()
    {
        var (app, errors) = Apply(new CordApp(Key: "budget"), """
        {"ops":[
          {"op":"upsert_entity","entity":
            {"key":"scenario","label":"Scenario","fields":[{"key":"name","label":"Name","type":"text"}]}},
          {"op":"upsert_entity","entity":
            {"key":"hiring_line","label":"Hire","fields":[
              {"key":"scenario","label":"Scenario","type":"reference","target":"scenario"},
              {"key":"monthly_cost","label":"Cost","type":"money"},
              {"key":"start_month","label":"From","type":"date"},
              {"key":"end_month","label":"To","type":"date"}]}},
          {"op":"upsert_entity","entity":
            {"key":"period","label":"Period","fields":[
              {"key":"scenario","label":"Scenario","type":"reference","target":"scenario"},
              {"key":"start_date","label":"Start","type":"date"},
              {"key":"payroll_cost","label":"People cost","type":"money",
               "aggregate":{"op":"sum","of":"hiring_line","field":"monthly_cost","over":"scenario",
                            "during":{"covering":{"from":"start_month","to":"end_month","myPoint":"start_date"}}}}]}}]}
        """);

        Assert.Empty(errors);

        var doc = (JsonObject)CordLower.Lower(app);
        var period = (doc["entities"] as JsonArray)!.OfType<JsonObject>().Single(e => (string?)e["key"] == "period");
        var payroll = (period["fields"] as JsonArray)!.OfType<JsonObject>().Single(f => (string?)f["key"] == "payroll_cost");

        Assert.Equal(
            """{"rollup":{"entity":"hiring_line","via":"scenario","match":"scenario","op":"sum","field":"monthly_cost","window":{"from":"start_month","to":"end_month","against":"start_date"}}}""",
            payroll["computed"]!.ToJsonString());
    }

    [Fact]
    public void Fields_can_be_added_and_removed_without_restating_the_app()
    {
        var start = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":
          {"key":"deal","label":"Deal","fields":[
            {"key":"name","label":"Name","type":"text"},
            {"key":"amount","label":"Amount","type":"money"}]}}]}
        """).App;

        var (app, errors) = Apply(start, """
        {"ops":[
          {"op":"upsert_field","entity":"deal","field":{"key":"stage","label":"Stage","type":"select",
            "role":"status","options":[{"value":"open","label":"Open"},{"value":"won","label":"Won"}]}},
          {"op":"remove","entity":"deal","field":"name"}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["amount", "stage"], app.EntityList[0].FieldList.Select(f => f.Key));
    }

    [Fact]
    public void There_is_no_rename()
    {
        Assert.DoesNotContain("rename", CordOps.DomainOpNames);
        Assert.DoesNotContain("rename", CordOps.DomainOpsSchema().ToJsonString());

        var (_, errors) = Apply(new CordApp(Key: "a"),
            """{"ops":[{"op":"rename","entity":"deal","to":"opportunity"}]}""");
        Assert.Equal(CordErrorCode.MalformedOperation, Assert.Single(errors).Code);
    }

    [Fact]
    public void An_upsert_leaves_everything_it_does_not_name_alone()
    {
        var start = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
          {"key":"name","label":"Name","type":"text"},
          {"key":"amount","label":"Amount","type":"money"}]}}]}
        """).App;

        var (app, errors) = Apply(start, """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"contact","label":"Contact","fields":[
            {"key":"name","label":"Name","type":"text"}]}},
          {"op":"upsert_field","entity":"deal","field":{"key":"amount","label":"Value","type":"money",
            "currency":"EUR"}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["deal", "contact"], app.EntityList.Select(e => e.Key));

        var deal = app.EntityList[0];
        Assert.Equal(["name", "amount"], deal.FieldList.Select(f => f.Key));
        Assert.Equal("Value", deal.FieldList[1].Label);
    }

    [Fact]
    public void Upserting_an_existing_entity_replaces_it_in_place()
    {
        var start = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
            {"key":"n","label":"N","type":"text"}]}},
          {"op":"upsert_entity","entity":{"key":"contact","label":"Contact","fields":[
            {"key":"n","label":"N","type":"text"}]}}]}
        """).App;

        var (app, errors) = Apply(start, """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Opportunity","fields":[
          {"key":"n","label":"N","type":"text"}]}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["deal", "contact"], app.EntityList.Select(e => e.Key));
        Assert.Equal("Opportunity", app.EntityList[0].Label);
    }

    [Fact]
    public void Operations_apply_in_the_order_given()
    {
        var (app, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
            {"key":"n","label":"N","type":"text"}]}},
          {"op":"upsert_field","entity":"deal","field":{"key":"extra","label":"Extra","type":"text"}},
          {"op":"remove","entity":"deal","field":"n"}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["extra"], app.EntityList[0].FieldList.Select(f => f.Key));
    }

    [Fact]
    public void An_operation_against_an_entity_that_does_not_exist_names_the_operation()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_field","entity":"ghost","field":{"key":"x","label":"X","type":"text"}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.UnknownEntity, error.Code);
        Assert.Equal(0, error.OperationIndex);
    }

    [Fact]
    public void An_unreadable_operation_names_its_index_rather_than_failing_the_batch()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"ok","label":"OK","fields":[{"key":"n","label":"N","type":"text"}]}},
          {"op":"invent_something"}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.MalformedOperation, error.Code);
        Assert.Equal(1, error.OperationIndex);
    }

    [Fact]
    public void An_ambiguous_join_is_refused_with_the_candidates_named()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"invoice","label":"Invoice","fields":[
            {"key":"total","label":"Total","type":"money",
             "aggregate":{"op":"sum","of":"transfer","field":"amount","over":"mine"}}]}},
          {"op":"upsert_entity","entity":{"key":"transfer","label":"Transfer","fields":[
            {"key":"from_invoice","label":"From","type":"reference","target":"invoice"},
            {"key":"to_invoice","label":"To","type":"reference","target":"invoice"},
            {"key":"amount","label":"Amount","type":"money"}]}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.AmbiguousJoin, error.Code);
        Assert.Equal(["from_invoice", "to_invoice"], error.Candidates);
    }

    [Fact]
    public void A_relationship_that_is_not_a_reference_is_refused()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"period","label":"Period","fields":[
            {"key":"label","label":"Label","type":"text"},
            {"key":"total","label":"Total","type":"money",
             "aggregate":{"op":"sum","of":"line","field":"amount","over":"label"}}]}},
          {"op":"upsert_entity","entity":
            {"key":"line","label":"Line","fields":[{"key":"amount","label":"Amount","type":"money"}]}}]}
        """);

        Assert.Equal(CordErrorCode.UnknownRelationship, Assert.Single(errors).Code);
    }

    [Fact]
    public void An_aggregate_over_an_entity_that_does_not_exist_is_refused()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":
          {"key":"period","label":"Period","fields":[
            {"key":"total","label":"Total","type":"money",
             "aggregate":{"op":"sum","of":"nothing_here","field":"amount","over":"mine"}}]}}]}
        """);

        Assert.Equal(CordErrorCode.UnknownEntity, Assert.Single(errors).Code);
    }

    [Fact]
    public void Counting_takes_no_field_and_summing_requires_one()
    {
        var (_, counted) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"invoice","label":"Invoice","fields":[
            {"key":"n","label":"N","type":"integer",
             "aggregate":{"op":"count","of":"line","field":"amount","over":"mine"}}]}},
          {"op":"upsert_entity","entity":{"key":"line","label":"Line","fields":[
            {"key":"invoice","label":"Invoice","type":"reference","target":"invoice"},
            {"key":"amount","label":"Amount","type":"money"}]}}]}
        """);
        Assert.Equal(CordErrorCode.AggregateFieldMismatch, Assert.Single(counted).Code);

        var (_, summed) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"invoice","label":"Invoice","fields":[
            {"key":"n","label":"N","type":"money",
             "aggregate":{"op":"sum","of":"line","over":"mine"}}]}},
          {"op":"upsert_entity","entity":{"key":"line","label":"Line","fields":[
            {"key":"invoice","label":"Invoice","type":"reference","target":"invoice"},
            {"key":"amount","label":"Amount","type":"money"}]}}]}
        """);
        Assert.Equal(CordErrorCode.AggregateFieldMismatch, Assert.Single(summed).Code);
    }

    [Fact]
    public void An_expression_that_does_not_parse_is_refused_in_the_expression_engines_own_words()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":
          {"key":"deal","label":"Deal","fields":[
            {"key":"amount","label":"Amount","type":"money"},
            {"key":"broken","label":"Broken","type":"money","expr":"amount * * 2"}]}}]}
        """);

        Assert.Equal(CordErrorCode.InvalidExpression, Assert.Single(errors).Code);
    }

    [Fact]
    public void An_expression_naming_a_field_that_is_not_there_is_refused()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":
          {"key":"deal","label":"Deal","fields":[
            {"key":"amount","label":"Amount","type":"money"},
            {"key":"doubled","label":"Doubled","type":"money","expr":"amont * 2"}]}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.InvalidExpression, error.Code);
        Assert.Contains("amont", error.Message);
    }

    [Fact]
    public void The_same_key_twice_inside_one_entity_is_refused()
    {
        Assert.Equal(CordErrorCode.DuplicateField, Assert.Single(Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
          {"key":"n","label":"N","type":"text"},
          {"key":"n","label":"Again","type":"text"}]}}]}
        """).Errors).Code);
    }

    [Fact]
    public void A_duplicate_entity_key_carried_in_from_a_document_is_refused()
    {
        var imported = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"deal","label":"Deal","fields":[{"key":"n","label":"N","type":"text"}]},
          {"key":"deal","label":"Deal again","fields":[{"key":"n","label":"N","type":"text"}]}]}
        """));

        var (_, errors) = Apply(imported, """
        {"ops":[{"op":"upsert_field","entity":"deal","field":{"key":"x","label":"X","type":"text"}}]}
        """);

        Assert.Equal(CordErrorCode.DuplicateEntity, Assert.Single(errors).Code);
    }

    [Fact]
    public void Removing_a_field_that_is_not_there_is_refused()
    {
        var start = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
          {"key":"n","label":"N","type":"text"}]}}]}
        """).App;

        Assert.Equal(CordErrorCode.UnknownField, Assert.Single(Apply(start,
            """{"ops":[{"op":"remove","entity":"deal","field":"ghost"}]}""").Errors).Code);

        var (app, errors) = Apply(start, """
        {"ops":[{"op":"upsert_field","entity":"deal","field":{"key":"fresh","label":"F","type":"text"}}]}
        """);
        Assert.Empty(errors);
        Assert.Equal(["n", "fresh"], app.EntityList[0].FieldList.Select(f => f.Key));
    }

    [Fact]
    public void A_screen_section_for_an_entity_that_does_not_exist_is_refused()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[{"key":"n","label":"N","type":"text"}]}},
          {"op":"upsert_screen","screen":{"key":"home","label":"Home","sections":[
            {"kind":"list","of":"deal"},
            {"kind":"metric","of":"ghost","value":{"op":"count"}}]}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.UnknownScreenEntity, error.Code);
        Assert.Contains("ghost", error.Message);
    }

    [Fact]
    public void Removing_an_entity_that_is_not_there_is_refused()
    {
        Assert.Equal(CordErrorCode.UnknownEntity,
            Assert.Single(Apply(new CordApp(Key: "a"), """{"ops":[{"op":"remove","entity":"ghost"}]}""").Errors).Code);
    }

    [Fact]
    public void Every_error_code_is_produced_by_a_test_in_this_suite()
    {
        var covered = new[]
        {
            CordErrorCode.UnknownEntity, CordErrorCode.UnknownField, CordErrorCode.DuplicateEntity,
            CordErrorCode.DuplicateField, CordErrorCode.UnknownRelationship, CordErrorCode.AmbiguousJoin,
            CordErrorCode.UnresolvableJoin, CordErrorCode.AggregateFieldMismatch,
            CordErrorCode.InvalidExpression, CordErrorCode.UnknownScreenEntity,
            CordErrorCode.UnknownScreen, CordErrorCode.UnknownBehaviour,
            CordErrorCode.ImportedScreensNotEditable,
            CordErrorCode.CalendarNeedsDateField, CordErrorCode.ConflictingInitialState,
            CordErrorCode.NestedSplit,
            CordErrorCode.UnknownState, CordErrorCode.MalformedOperation,
            CordErrorCode.UnknownTab, CordErrorCode.DuplicateTab, CordErrorCode.OutsideScope,
        };

        Assert.Equal(
            Enum.GetValues<CordErrorCode>().OrderBy(x => x).ToList(),
            covered.OrderBy(x => x).ToList());
    }

    [Fact]
    public void An_unresolvable_join_is_refused()
    {
        var (_, errors) = Apply(new CordApp(Key: "a"), """
        {"ops":[
          {"op":"upsert_entity","entity":{"key":"invoice","label":"Invoice","fields":[
            {"key":"total","label":"Total","type":"money",
             "aggregate":{"op":"sum","of":"note","field":"amount","over":"mine"}}]}},
          {"op":"upsert_entity","entity":
            {"key":"note","label":"Note","fields":[{"key":"amount","label":"Amount","type":"money"}]}}]}
        """);

        Assert.Equal(CordErrorCode.UnresolvableJoin, Assert.Single(errors).Code);
    }

    private static CordApp Deals() => CordImport.Import(JsonNode.Parse(
        """
        {"key":"crm","name":"CRM","version":"1.0.0","entities":[
          {"key":"deal","label":"Deal","fields":[
            {"key":"title","label":"Title","type":"text"},
            {"key":"status","label":"Status","type":"select","options":[
              {"value":"open","label":"Open"},{"value":"won","label":"Won"}]},
            {"key":"won_on","label":"Won on","type":"date"}]}]}
        """));

    [Fact]
    public void A_transition_carries_its_effects_and_the_command_is_derived()
    {
        var (app, errors) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"deal","stateField":"status","initialState":"open",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won","terminal":true}],
          "transitions":[{"key":"win","label":"Mark as won","from":["open"],"to":"won",
            "successMessage":"Deal won",
            "effects":[{"type":"updateRecord","set":{"won_on":"{{today}}"}}]}]}}]}
        """);

        Assert.Empty(errors);

        var lowered = (JsonObject)CordLower.Lower(app);
        var transition = lowered["processes"]![0]!["transitions"]![0]!;
        var command = (JsonObject)lowered["commands"]![0]!;

        Assert.Equal("win", (string)transition["command"]!);
        Assert.Equal("win", (string)command["key"]!);
        Assert.Equal("deal", (string)command["entity"]!);
        Assert.Equal("Mark as won", (string)command["label"]!);
        Assert.Equal("Deal won", (string)command["successMessage"]!);
        Assert.Equal("{{today}}", (string)command["effects"]![0]!["set"]!["won_on"]!);

        Assert.Equal("won", (string)transition["to"]!);
    }

    [Fact]
    public void A_transition_with_nothing_to_do_lowers_to_no_command()
    {
        var (app, _) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"deal","stateField":"status",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won"}],
          "transitions":[{"key":"win","label":"Won","from":["open"],"to":"won"}]}}]}
        """);

        var lowered = (JsonObject)CordLower.Lower(app);
        Assert.Null(lowered["commands"]);
        Assert.Null(lowered["processes"]![0]!["transitions"]![0]!["command"]);
    }

    [Fact]
    public void An_approval_can_refuse_the_person_who_filed_it()
    {
        var (app, errors) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"deal","stateField":"status","initialState":"open",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won"}],
          "transitions":[{"key":"approve","label":"Approve","from":["open"],"to":"won",
            "when":{"field":"owner","operator":"neq","value":"{{actor.id}}"}}]}}]}
        """);

        Assert.Empty(errors);

        var lowered = (JsonObject)CordLower.Lower(app);
        var command = (JsonObject)lowered["commands"]![0]!;
        Assert.Equal("approve", (string)command["key"]!);
        Assert.Equal("owner", (string)command["when"]!["field"]!);
        Assert.Equal("neq", (string)command["when"]!["operator"]!);
        Assert.Equal("{{actor.id}}", (string)command["when"]!["value"]!);
    }

    [Fact]
    public void A_transition_carrying_only_a_guard_still_emits_the_command_that_enforces_it()
    {
        var (app, _) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"deal","stateField":"status",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won"}],
          "transitions":[{"key":"win","label":"Won","from":["open"],"to":"won",
            "when":{"field":"owner","operator":"neq","value":"{{actor.id}}"}}]}}]}
        """);

        var lowered = (JsonObject)CordLower.Lower(app);

        var command = Assert.IsType<JsonObject>(Assert.Single(lowered["commands"]!.AsArray())!);
        Assert.Equal("win", (string)command["key"]!);
        Assert.NotNull(command["when"]);
        Assert.Equal("win", (string)lowered["processes"]![0]!["transitions"]![0]!["command"]!);
    }

    [Fact]
    public void A_transition_to_an_undeclared_state_is_refused()
    {
        var (_, errors) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"deal","stateField":"status",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won"}],
          "transitions":[{"key":"lose","label":"Lost","from":["open"],"to":"lost"}]}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.UnknownState, error.Code);
        Assert.Contains("lost", error.Message);
        Assert.Equal(0, error.OperationIndex);
    }

    [Fact]
    public void Behaviour_naming_an_unknown_entity_is_refused()
    {
        var (_, errors) = Apply(Deals(), """
        {"ops":[{"op":"upsert_action","action":
          {"key":"send","label":"Send","entity":"invoice",
           "effects":[{"type":"notify","to":"{{record.owner}}","message":"x"}]}}]}
        """);

        var error = Assert.Single(errors);
        Assert.Equal(CordErrorCode.UnknownEntity, error.Code);
        Assert.Contains("invoice", error.Message);
    }

    [Fact]
    public void Automations_and_roles_lower_to_their_sections()
    {
        var (app, errors) = Apply(Deals(), """
        {"ops":[
          {"op":"upsert_automation","automation":
            {"key":"stamp","name":"Stamp won date","on":"record.updated","entity":"deal",
             "field":"status","when":{"field":"status","operator":"eq","value":"won"},
             "effects":[{"type":"updateRecord","setIfEmpty":true,"set":{"won_on":"{{now}}"}}]}},
          {"op":"upsert_role","role":
            {"key":"rep","name":"Sales rep","grants":[
              {"entity":"deal","create":true,"read":true,"update":true,"commands":["win"]}]}}]}
        """);

        Assert.Empty(errors);
        var lowered = (JsonObject)CordLower.Lower(app);

        var workflow = (JsonObject)lowered["workflows"]![0]!;
        Assert.Equal("field.changed", (string)workflow["trigger"]!["event"]!);
        Assert.Equal("deal", (string)workflow["trigger"]!["entity"]!);
        Assert.Equal("status", (string)workflow["trigger"]!["field"]!);
        Assert.True((bool)workflow["effects"]![0]!["setIfEmpty"]!);

        var grant = (JsonObject)lowered["roles"]![0]!["grants"]![0]!;
        Assert.Equal("deal", (string)grant["entity"]!);
        Assert.Equal("win", (string)grant["commands"]![0]!);
    }

    [Fact]
    public void A_second_role_does_not_replace_the_first()
    {
        var (one, _) = Apply(Deals(), """
        {"ops":[{"op":"upsert_role","role":{"key":"rep","name":"Rep","grants":[
          {"entity":"deal","read":true,"create":true}]}}]}
        """);

        var (two, errors) = Apply(one, """
        {"ops":[{"op":"upsert_role","role":{"key":"manager","name":"Manager","grants":[
          {"entity":"deal","read":true,"update":true,"commands":["win"]}]}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["rep", "manager"], two.RoleList.Select(r => r.Key));

        var (gone, removeErrors) = Apply(two, """
        {"ops":[{"op":"remove_behaviour","kind":"role","key":"rep"}]}
        """);
        Assert.Empty(removeErrors);
        Assert.Equal(["manager"], gone.RoleList.Select(r => r.Key));

        Assert.Equal(CordErrorCode.UnknownBehaviour, Assert.Single(Apply(gone, """
        {"ops":[{"op":"remove_behaviour","kind":"role","key":"rep"}]}
        """).Errors).Code);
    }

    [Fact]
    public void Restating_a_lifecycle_replaces_it()
    {
        var (once, _) = Apply(Deals(), """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{"entity":"deal","stateField":"status",
          "states":[{"key":"open","label":"Open"}],"transitions":[]}}]}
        """);

        var (twice, _) = Apply(once, """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{"entity":"deal","stateField":"status",
          "states":[{"key":"open","label":"Open"},{"key":"won","label":"Won"}],"transitions":[]}}]}
        """);

        var process = Assert.Single(twice.ProcessList);
        Assert.Equal(2, process.StateList.Count);
    }

    [Fact]
    public void One_screen_can_draw_on_several_entities()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"budget","name":"Budget","version":"1.0.0","entities":[
          {"key":"scenario","label":"Scenario","fields":[{"key":"name","label":"Name","type":"text"}]},
          {"key":"hire","label":"Hire","fields":[{"key":"cost","label":"Cost","type":"money"},
            {"key":"status","label":"Status","type":"select","options":[
              {"value":"open","label":"Open"},{"value":"filled","label":"Filled"}]}]},
          {"key":"funding_round","label":"Round","fields":[{"key":"amount","label":"Amount","type":"money"}]},
          {"key":"cost_line","label":"Cost line","fields":[
            {"key":"amount","label":"Amount","type":"money"},
            {"key":"behaviour","label":"Behaviour","type":"select","options":[
              {"value":"fixed","label":"Fixed"},{"value":"variable","label":"Variable"}]}]}]}
        """));

        var (app, errors) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":
          {"key":"investor_overview","label":"Investor Overview","subject":"scenario","sections":[
            {"kind":"metric","of":"scenario","label":"Scenarios","value":{"op":"count"}},
            {"kind":"metric","of":"funding_round","label":"Funding closed","value":{"op":"sum","field":"amount"}},
            {"kind":"chart","of":"cost_line","label":"Cost by behaviour",
             "groupBy":"behaviour","value":{"op":"sum","field":"amount"}},
            {"kind":"list","of":"hire","label":"Open hires","view":"table",
             "filter":[{"field":"status","operator":"eq","value":"open"}],
             "columns":["cost"]}]}}]}
        """);

        Assert.Empty(errors);

        var lowered = (JsonObject)CordLower.Lower(app);
        var page = (JsonObject)lowered["pages"]![0]!;

        Assert.Equal("investor_overview", (string)page["key"]!);
        Assert.Equal("scenario", (string)page["entity"]!);

        var blocks = (JsonArray)page["blocks"]!;
        Assert.Equal("row", (string)blocks[0]!["kind"]!);
        Assert.Equal(2, ((JsonArray)blocks[0]!["blocks"]!).Count);
        Assert.Equal("card", (string)blocks[1]!["kind"]!);
        Assert.Equal("view", (string)blocks[2]!["kind"]!);

        var stat = blocks[0]!["blocks"]![1]!;
        Assert.Equal("funding_round", (string)stat["blocks"]![0]!["source"]!["entity"]!);
        Assert.Equal("sum", (string)stat["blocks"]![0]!["source"]!["aggregate"]!["op"]!);

        var view = (JsonObject)lowered["views"]![0]!;
        Assert.Equal("hire", (string)view["entity"]!);
        Assert.Equal("open", (string)view["filters"]![0]!["value"]!);
        Assert.Equal("cost", (string)view["config"]!["columns"]![0]!);
    }

    [Fact]
    public void Two_lists_of_one_entity_get_distinct_view_keys()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"ticket","label":"Ticket","fields":[
            {"key":"done","label":"Done","type":"boolean"}]}]}
        """));

        var (app, errors) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":
          {"key":"work","label":"Work","sections":[
            {"kind":"list","of":"ticket","label":"Open","filter":[{"field":"done","operator":"eq","value":false}]},
            {"kind":"list","of":"ticket","label":"Done","filter":[{"field":"done","operator":"eq","value":true}]}]}}]}
        """);

        Assert.Empty(errors);
        var views = (JsonArray)((JsonObject)CordLower.Lower(app))["views"]!;

        Assert.Equal(2, views.Count);
        Assert.Equal(2, views.Select(v => (string)v!["key"]!).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Screens_cannot_be_authored_over_an_app_whose_screens_were_imported()
    {
        var imported = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0",
         "entities":[{"key":"deal","label":"Deal","fields":[{"key":"n","label":"N","type":"text"}]}],
         "views":[{"key":"old_view","label":"Old","type":"table","entity":"deal"}],
         "pages":[{"key":"old_page","label":"Old page","entity":"deal",
                   "blocks":[{"kind":"view","view":"old_view"}]}]}
        """));

        Assert.NotNull(imported.Raw?["pages"]);
        var before = CordLower.Lower(imported).ToJsonString();

        foreach (var op in new[]
        {
            """
            {"ops":[{"op":"upsert_screen","screen":
              {"key":"brand_new","label":"Brand new","sections":[
                {"kind":"list","of":"deal","label":"Deals"}]}}]}
            """,
            """{"ops":[{"op":"remove_screen","key":"old_page"}]}""",
        })
        {
            var (app, errors) = Apply(imported, op);
            Assert.Equal(CordErrorCode.ImportedScreensNotEditable, Assert.Single(errors).Code);

            Assert.Equal(before, CordLower.Lower(app).ToJsonString());
        }
    }

    [Fact]
    public void An_imported_apps_domain_is_still_editable()
    {
        var imported = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0",
         "entities":[{"key":"deal","label":"Deal","fields":[{"key":"n","label":"N","type":"text"}]}],
         "views":[{"key":"old_view","label":"Old","type":"table","entity":"deal"}],
         "pages":[{"key":"old_page","label":"Old page","entity":"deal",
                   "blocks":[{"kind":"view","view":"old_view"}]}]}
        """));

        var (app, errors) = Apply(imported, """
        {"ops":[{"op":"upsert_field","entity":"deal","field":{"key":"amount","label":"Amount","type":"money"}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["n", "amount"], app.EntityList[0].FieldList.Select(f => f.Key));
        Assert.Equal(["old_page"],
            ((JsonArray)((JsonObject)CordLower.Lower(app))["pages"]!).Select(p => (string)p!["key"]!));
    }

    [Fact]
    public void A_second_screen_does_not_replace_the_first()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"ticket","label":"Ticket","fields":[{"key":"n","label":"N","type":"text"}]}]}
        """));

        var (one, _) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"work","label":"Work","sections":[
          {"kind":"list","of":"ticket","label":"All"}]}}]}
        """);

        var (two, errors) = Apply(one, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"overview","label":"Overview","sections":[
          {"kind":"metric","of":"ticket","label":"Tickets","value":{"op":"count"}}]}}]}
        """);

        Assert.Empty(errors);
        Assert.Equal(["work", "overview"], two.Screens!.Select(s => s.Key));

        var (gone, removeErrors) = Apply(two, """{"ops":[{"op":"remove_screen","key":"work"}]}""");
        Assert.Empty(removeErrors);
        Assert.Equal(["overview"], gone.Screens!.Select(s => s.Key));

        Assert.Equal(CordErrorCode.UnknownScreen,
            Assert.Single(Apply(gone, """{"ops":[{"op":"remove_screen","key":"work"}]}""").Errors).Code);
    }

    [Fact]
    public void A_tab_is_revised_without_restating_its_siblings()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"},
            {"key":"cost","label":"Cost","type":"number"}]}]}
        """));

        var (screen, errors) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring Plan",
          "sections":[{"key":"headline","kind":"metric","of":"hire","label":"Planned heads",
                       "value":{"op":"count"}}],
          "tabs":[
            {"key":"plan","label":"Plan","sections":[{"key":"plan_table","kind":"list","of":"hire","label":"Lines"}]},
            {"key":"cost","label":"Cost","sections":[{"key":"cost_chart","kind":"chart","of":"hire","label":"By team",
                                                      "value":{"op":"sum","field":"cost"},"groupBy":"team"}]}]}}]}
        """);
        Assert.Empty(errors);

        var before = screen.Screens![0].TabList.Single(t => t.Key == "plan");

        var (revised, tabErrors) = Apply(screen, """
        {"ops":[{"op":"upsert_screen_tab","screen":"hiring","tab":{"key":"cost","label":"Cost by team",
          "sections":[{"key":"cost_chart","kind":"chart","of":"hire","label":"Annual cost per team",
                       "value":{"op":"sum","field":"cost"},"groupBy":"team","visual":"bar"}]}}]}
        """);

        Assert.Empty(tabErrors);
        var after = revised.Screens![0];
        Assert.Equal(["plan", "cost"], after.TabList.Select(t => t.Key));
        Assert.Equal(before, after.TabList.Single(t => t.Key == "plan"));
        Assert.Equal("Cost by team", after.TabList.Single(t => t.Key == "cost").Label);
        Assert.Equal(["headline"], after.SectionList.Select(s => s.Key));

        var page = (JsonObject)((JsonArray)((JsonObject)CordLower.Lower(revised))["pages"]!)[0]!;
        var tabsBlock = ((JsonArray)page["blocks"]!).OfType<JsonObject>()
            .Single(b => (string?)b["kind"] == "tabs");
        Assert.Equal(["plan", "cost"],
            ((JsonArray)tabsBlock["tabs"]!).Select(t => (string)t!["key"]!));
    }

    [Fact]
    public void A_tab_operation_names_a_screen_and_a_tab_that_exist()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"hire","label":"Hire","fields":[{"key":"team","label":"Team","type":"text"}]}]}
        """));

        var (screen, _) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring",
          "tabs":[{"key":"plan","label":"Plan",
                   "sections":[{"key":"t","kind":"list","of":"hire","label":"Lines"}]}]}}]}
        """);

        Assert.Equal(CordErrorCode.UnknownScreen, Assert.Single(Apply(screen, """
        {"ops":[{"op":"upsert_screen_tab","screen":"nope","tab":{"key":"x","label":"X",
          "sections":[{"key":"s","kind":"list","of":"hire","label":"L"}]}}]}
        """).Errors).Code);

        Assert.Equal(CordErrorCode.UnknownTab, Assert.Single(Apply(screen, """
        {"ops":[{"op":"remove_screen_tab","screen":"hiring","tab":"nope"}]}
        """).Errors).Code);

        var (gone, removed) = Apply(screen, """
        {"ops":[{"op":"remove_screen_tab","screen":"hiring","tab":"plan"}]}
        """);
        Assert.Empty(removed);
        Assert.Empty(gone.Screens![0].TabList);
    }

    [Fact]
    public void Two_tabs_may_not_share_a_key()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"hire","label":"Hire","fields":[{"key":"team","label":"Team","type":"text"}]}]}
        """));

        var (_, errors) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring","tabs":[
          {"key":"plan","label":"Plan","sections":[{"key":"a","kind":"list","of":"hire","label":"A"}]},
          {"key":"plan","label":"Plan again","sections":[{"key":"b","kind":"list","of":"hire","label":"B"}]}]}}]}
        """);

        Assert.Equal(CordErrorCode.DuplicateTab, Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("ask.title", """
    [{"op":"upsert_action","action":{"key":"a","label":"A","entity":"deal",
      "ask":{"title":"Why?","fields":["note"]},
      "effects":[{"type":"notify","to":"{{record.owner}}","message":"x"}]}}]
    """, "title")]
    [InlineData("aggregate.during.covering.unknown", """
    [{"op":"upsert_entity","entity":{"key":"period","label":"Period","fields":[
      {"key":"total","label":"Total","type":"money",
       "aggregate":{"op":"sum","of":"line","field":"amount","over":"mine",
                    "during":{"covering":{"from":"a","myPoint":"b","untilForever":true}}}}]}}]
    """, "untilForever")]
    [InlineData("screen.sections.sort.unknown", """
    [{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
      {"kind":"list","of":"deal","sort":[{"field":"n","direction":"asc","nulls":"last"}]}]}}]
    """, "nulls")]
    [InlineData("screen.columns", """
    [{"op":"upsert_screen","screen":{"key":"s","label":"S","columns":["a"],"sections":[
      {"kind":"list","of":"deal"}]}}]
    """, "columns")]
    public void An_unknown_or_misplaced_property_is_refused(string what, string ops, string property)
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"deal","label":"Deal","fields":[{"key":"n","label":"N","type":"text"}]}]}
        """));
        var before = CordLower.Lower(draft).ToJsonString();

        var prepared = CordTransaction.Prepare(draft, JsonNode.Parse(ops));
        foreach (var e in prepared.Errors) output.WriteLine($"{what}: {e}");

        Assert.False(prepared.Ok);
        Assert.All(prepared.Errors, e => Assert.Equal(CordErrorCode.MalformedOperation, e.Code));
        Assert.All(prepared.Errors, e => Assert.Equal(0, e.OperationIndex));
        Assert.Contains(prepared.Errors,
            e => e.Where.Contains(property, StringComparison.Ordinal)
                 || e.Message.Contains(property, StringComparison.Ordinal));

        Assert.Equal(before, CordLower.Lower(prepared.Next).ToJsonString());
    }

    [Fact]
    public void A_batch_with_one_bad_operation_applies_none_of_it()
    {
        var draft = new CordApp(Key: "a");
        var prepared = CordTransaction.Prepare(draft, JsonNode.Parse("""
        [{"op":"upsert_entity","entity":{"key":"good","label":"Good","fields":[
           {"key":"n","label":"N","type":"text"}]}},
         {"op":"upsert_entity","entity":{"key":"bad","label":"Bad","colour":"#fff","fields":[
           {"key":"n","label":"N","type":"text"}]}}]
        """));

        Assert.False(prepared.Ok);
        Assert.Equal(1, Assert.Single(prepared.Errors, e => e.Message.Contains("colour", StringComparison.Ordinal)
            || e.Where.Contains("colour", StringComparison.Ordinal)).OperationIndex);
        Assert.Empty(prepared.Next.EntityList);
    }

    [Fact]
    public void A_calendar_that_does_not_say_which_date_is_refused()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"claim","label":"Claim","fields":[
            {"key":"submitted_on","label":"Submitted","type":"date"},
            {"key":"due_by","label":"Due","type":"date"}]}]}
        """));

        var (_, errors) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"d","label":"D","sections":[
          {"kind":"list","of":"claim","label":"Decisions due","view":"calendar"}]}}]}
        """);

        var error = Assert.Single(errors);
        output.WriteLine(error.ToString());
        Assert.Equal(CordErrorCode.CalendarNeedsDateField, error.Code);

        var (ok, none) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"d","label":"D","sections":[
          {"kind":"list","of":"claim","label":"Decisions due","view":"calendar","dateField":"due_by"}]}}]}
        """);
        Assert.Empty(none);
        Assert.Equal("due_by", ok.Screens![0].SectionList[0].DateField);
    }

    [Fact]
    public void A_governed_default_that_contradicts_the_lifecycle_is_refused()
    {
        static CordApp Draft(string dflt) => CordImport.Import(JsonNode.Parse($$"""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"claim","label":"Claim","fields":[
            {"key":"status","label":"Status","type":"select","role":"status","default":"{{dflt}}",
             "options":[{"value":"draft","label":"Draft"},{"value":"sent","label":"Sent"}]}]}]}
        """));

        const string Lifecycle = """
        {"ops":[{"op":"upsert_lifecycle","lifecycle":{
          "entity":"claim","stateField":"status","initialState":"draft",
          "states":[{"key":"draft","label":"Draft"},{"key":"sent","label":"Sent"}],
          "transitions":[]}}]}
        """;

        var error = Assert.Single(Apply(Draft("sent"), Lifecycle).Errors);
        output.WriteLine(error.ToString());
        Assert.Equal(CordErrorCode.ConflictingInitialState, error.Code);
        Assert.Contains("draft", error.Message);
        Assert.Contains("sent", error.Message);

        var (app, none) = Apply(Draft("draft"), Lifecycle);
        Assert.Empty(none);
        var lowered = (JsonObject)CordLower.Lower(app);
        var status = lowered["entities"]![0]!["fields"]!.AsArray().OfType<JsonObject>()
            .Single(f => (string?)f["key"] == "status");
        Assert.Null(status["default"]);
    }

    [Theory]
    [InlineData("upsert_screen")]
    [InlineData("upsert_lifecycle")]
    [InlineData("upsert_role")]
    public void The_domain_tool_refuses_another_concerns_operation(string op)
    {
        var prepared = CordTransaction.Prepare(
            new CordApp(Key: "a"),
            JsonNode.Parse($$"""[{"op":"{{op}}"}]"""),
            [.. CordOps.DomainOpNames]);

        var error = Assert.Single(prepared.Errors);
        Assert.Equal(CordErrorCode.MalformedOperation, error.Code);
        Assert.Contains("tool", error.Message);
    }

    [Fact]
    public void The_domain_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: domain ops", CordOps.DomainOpsSchema());
        output.WriteLine(report.ToString());

        Assert.InRange(report.Bytes, 1, 12_000);
        Assert.Equal(6, report.Operations);
    }

    [Fact]
    public void The_ui_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: ui ops", CordOps.UiOpsSchema());
        output.WriteLine(report.ToString());

        Assert.InRange(report.Bytes, 1, 7_000);
        Assert.Equal(4, CordOps.UiOpNames.Count);
    }

    [Theory]
    [InlineData("blocks")]
    [InlineData("kind\":{\"const")]
    [InlineData("card")]
    [InlineData("row")]
    [InlineData("stack")]
    public void The_ui_schema_never_offers_a_block_tree(string forbidden)
    {
        var json = CordOps.UiOpsSchema().ToJsonString().Replace(" ", "");

        Assert.DoesNotContain($"\"{forbidden}\":{{", json);
    }

    [Fact]
    public void The_ui_schema_is_written_out_for_review()
    {
        var path = Path.Combine(Corpus.RepoRoot(), "schemas", "ops", "ui-ops.schema.json");
        var current = CordOps.UiOpsSchema()
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != current) File.WriteAllText(path, current);

        output.WriteLine($"wrote {current.Length:N0} bytes to {path}");
        Assert.Equal(current, File.ReadAllText(path));
    }

    [Fact]
    public void The_domain_schema_is_written_out_for_review()
    {
        var path = Path.Combine(Corpus.RepoRoot(), "schemas", "ops", "domain-ops.schema.json");
        var current = CordOps.DomainOpsSchema().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        if (existing != current) File.WriteAllText(path, current);

        output.WriteLine($"wrote {current.Length:N0} bytes to {path}");
        Assert.Equal(current, File.ReadAllText(path));
    }

    [Fact]
    public void The_behaviour_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: behaviour ops", CordOps.BehaviourOpsSchema());
        output.WriteLine(report.ToString());

        Assert.InRange(report.Bytes, 1, 9_500);
        Assert.Equal(5, report.Operations);
    }

    [Fact]
    public void The_concern_schemas_do_not_leak_into_each_other()
    {
        var schemas = new (string Concern, string Json, IReadOnlyList<string> Own)[]
        {
            ("domain", CordOps.DomainOpsSchema().ToJsonString(), CordOps.DomainOpNames),
            ("behaviour", CordOps.BehaviourOpsSchema().ToJsonString(), CordOps.BehaviourOpNames),
            ("ui", CordOps.UiOpsSchema().ToJsonString(), CordOps.UiOpNames),
        };

        foreach (var mine in schemas)
            foreach (var other in schemas.Where(x => x.Concern != mine.Concern))
                foreach (var op in other.Own)
                    Assert.DoesNotContain($"\"{op}\"", mine.Json);

        var all = schemas.SelectMany(x => x.Own).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("command")]
    [InlineData("commandKey")]
    [InlineData("emits")]
    public void The_behaviour_schema_never_offers_the_command_link(string forbidden)
    {
        var json = CordOps.BehaviourOpsSchema().ToJsonString().Replace(" ", "");
        Assert.DoesNotContain($"\"{forbidden}\":{{", json);
    }

    [Fact]
    public void The_behaviour_schema_is_written_out_for_review()
    {
        var path = Path.Combine(Corpus.RepoRoot(), "schemas", "ops", "behaviour-ops.schema.json");
        var current = CordOps.BehaviourOpsSchema()
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != current) File.WriteAllText(path, current);

        output.WriteLine($"wrote {current.Length:N0} bytes to {path}");
        Assert.Equal(current, File.ReadAllText(path));
    }

    [Fact]
    public void The_domain_schema_offers_exactly_the_domain_operations()
    {
        var ops = (CordOps.DomainOpsSchema()["properties"]!["ops"]!["items"]!["oneOf"] as JsonArray)!
            .Select(o => (string)o!["title"]!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(CordOps.DomainOpNames.OrderBy(x => x, StringComparer.Ordinal).ToList(), ops);
    }

    [Theory]
    [InlineData("via")]
    [InlineData("match")]
    [InlineData("against")]
    [InlineData("within")]
    [InlineData("targetEntity")]
    [InlineData("computed")]
    [InlineData("rollup")]
    public void The_schema_never_offers_machinery_the_lowerer_derives(string forbidden)
    {
        var json = CordOps.DomainOpsSchema().ToJsonString();

        Assert.DoesNotContain($"\"{forbidden}\":{{", json.Replace(" ", ""));
    }

    [Theory]
    [InlineData("view", "/$defs/view/properties/type")]
    [InlineData("automation trigger", "/$defs/workflow/properties/trigger/properties/event")]
    [InlineData("button style", "/$defs/command/properties/style")]
    [InlineData("button placement", "/$defs/command/properties/placements/items")]
    [InlineData("state phase", "/$defs/process/properties/states/items/properties/phase")]
    [InlineData("effect", "/$defs/effect/properties/type")]
    [InlineData("comparison", "/$defs/condition/properties/operator")]
    public void Every_word_cord_offers_lowers_to_a_value_the_platform_accepts(string name, string pointer)
    {
        var map = CordVocabulary.All.Single(m => m.Name == name);

        JsonNode? node = Schemas.AppDefinitionSchemaNode();
        foreach (var step in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node?[step];
            Assert.True(node is not null, $"'{pointer}' does not resolve — the schema moved at '{step}'");
        }

        var accepted = (node!["enum"] as JsonArray)!.Select(v => (string)v!).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"{name}: {string.Join(", ", map.Words)} → {string.Join(", ", map.LoweredValues)}");
        output.WriteLine($"{name}: platform accepts {string.Join(", ", accepted)}");

        foreach (var (word, lowered) in map.Pairs)
            Assert.True(accepted.Contains(lowered),
                $"Cord offers '{word}', which lowers to '{lowered}', which the platform does not accept");
    }

    [Theory]
    [InlineData("view", "/$defs/view/properties/type")]
    [InlineData("automation trigger", "/$defs/workflow/properties/trigger/properties/event")]
    [InlineData("button style", "/$defs/command/properties/style")]
    [InlineData("button placement", "/$defs/command/properties/placements/items")]
    [InlineData("state phase", "/$defs/process/properties/states/items/properties/phase")]
    [InlineData("effect", "/$defs/effect/properties/type")]
    [InlineData("comparison", "/$defs/condition/properties/operator")]
    [InlineData("chart visual", "/$defs/block_chart/properties/chartType")]
    public void Every_value_the_platform_accepts_is_offered_or_withheld_on_purpose(
        string name, string pointer)
    {
        var map = CordVocabulary.All.Single(m => m.Name == name);

        JsonNode? node = Schemas.AppDefinitionSchemaNode();
        foreach (var step in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node?[step];
            Assert.True(node is not null, $"'{pointer}' does not resolve — the schema moved at '{step}'");
        }

        var accepted = (node!["enum"] as JsonArray)!.Select(v => (string)v!).ToList();
        var offered = map.LoweredValues.ToHashSet(StringComparer.Ordinal);
        var withheld = map.Withheld.Select(w => w.Word).ToHashSet(StringComparer.Ordinal);

        output.WriteLine($"{name}: offers {offered.Count}, withholds {withheld.Count}, "
                         + $"platform has {accepted.Count}");
        foreach (var (word, because) in map.Withheld) output.WriteLine($"  withheld {word} — {because}");

        var unaccounted = accepted.Where(v => !offered.Contains(v) && !withheld.Contains(v)).ToList();

        Assert.True(unaccounted.Count == 0,
            $"the platform accepts {string.Join(", ", unaccounted)} for '{name}' and Cord neither "
            + "offers nor withholds it. Decide which: add it to the map if an author should be able "
            + "to write it, or to Withheld with the reason if not. An unexplained absence reads as a "
            + "broken tool and gets routed around.");

        Assert.Empty(offered.Intersect(withheld, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("view", "ui")]
    [InlineData("automation trigger", "behaviour")]
    [InlineData("button style", "behaviour")]
    [InlineData("button placement", "behaviour")]
    [InlineData("state phase", "behaviour")]
    public void The_schema_offers_exactly_the_maps_words(string name, string concern)
    {
        var map = CordVocabulary.All.Single(m => m.Name == name);
        var schema = (concern == "ui" ? CordOps.UiOpsSchema() : CordOps.BehaviourOpsSchema()).ToJsonString();

        foreach (var word in map.Words) Assert.Contains($"\"{word}\"", schema);
        foreach (var stray in new[] { "listHeader", "schedule.daily-ish", "muted", "accent" })
            if (!map.Words.Contains(stray)) Assert.DoesNotContain($"\"{stray}\"", schema);
    }

    [Fact]
    public void Every_screen_view_word_lowers_to_a_real_view_type()
    {
        var allowed = (Schemas.AppDefinitionSchemaNode()["$defs"]?["view"]?["properties"]?["type"]?["enum"]
                       ?? Schemas.AppDefinitionSchemaNode()["properties"]?["views"]?["items"]?["properties"]?["type"]?["enum"])
            as JsonArray;

        Assert.NotNull(allowed);
        var types = allowed!.Select(v => (string)v!).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"App Definition view types: {string.Join(", ", types.OrderBy(x => x, StringComparer.Ordinal))}");

        foreach (var word in CordVocabulary.Views.Words)
            Assert.Contains(CordVocabulary.Views.Lower(word)!, types);

        var offered = (CordOps.UiOpsSchema()["$defs"]!["section"]!["properties"]!["view"]!["enum"]
            as JsonArray)!
            .Select(v => (string)v!)
            .ToList();

        Assert.Equal(CordVocabulary.Views.Words.ToList(), offered);
    }
}
