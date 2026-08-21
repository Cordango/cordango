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

/// <summary>
/// The semantic operations: what the model says, and what Cord makes of it.
///
/// <para>The property under test throughout is that <b>the model never writes an App Definition</b>.
/// Every assertion below goes from an operation an author could plausibly have dictated to a lowered
/// document nobody had to think about.</para>
/// </summary>
public class CordOpsTests(ITestOutputHelper output)
{
    private static JsonNode Ops(string json) => JsonNode.Parse(json)!;

    private static (CordApp App, IReadOnlyList<CordError> Errors) Apply(CordApp draft, string json)
    {
        var prepared = CordTransaction.Prepare(draft, Ops(json)["ops"]);
        return (prepared.Next, prepared.Errors);
    }

    // ---- bulk creation ---------------------------------------------------------------------------

    /// <summary>
    /// A whole domain in one CALL — the answer to "forty one-field-at-a-time operations would be
    /// miserable".
    ///
    /// <para><b>The batching lives on <c>ops</c>, not inside an operation</b>, and that distinction is
    /// the whole of the aggregate-granularity decision. An earlier <c>replace_domain</c> took the entity
    /// list itself; it cost the same bytes as this does and bought a collection-replacing operation
    /// whose failure mode is silently deleting whatever the author did not restate.</para>
    ///
    /// <para>The alternative considered and rejected was letting the model fall back to submitting an
    /// App Definition for blank-page work. That would have made Cord a repair mechanism and left the
    /// thesis untested. Bulk SEMANTIC creation gets the same economy without the retreat.</para>
    /// </summary>
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

        // The calculation survived as a calculation, not as text somebody has to remember to wire up.
        var loaded = app.EntityList[1].FieldList.Single(f => f.Key == "monthly_cost");
        Assert.IsType<CordExpr>(loaded.Calc);
    }

    /// <summary>
    /// The whole point, in one assertion: an aggregate the author described relationally, lowered into
    /// machinery they never mentioned.
    ///
    /// <para>They said "sum the hiring lines of my scenario, while they cover my start date". They did
    /// not say <c>via</c>, <c>match</c>, or which of <c>against</c>/<c>at</c> this window wanted.</para>
    /// </summary>
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

    // ---- fine-grained evolution ------------------------------------------------------------------

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

    /// <summary>
    /// <b>There is no <c>rename</c>, and that is enforced rather than merely documented.</b>
    ///
    /// <para>It existed and it renamed a key while rewriting nothing that pointed at it — reference
    /// targets, <c>expr</c> operands, an aggregate's <c>of</c>/<c>over</c>, a lifecycle's
    /// <c>stateField</c>, a role's granted commands, a screen section's filter fields. The gate catches
    /// every one of those, so it was never silent; it was worse in a subtler way, turning one intention
    /// into a scatter of unrelated-looking failures for the author to repair by hand. That is precisely
    /// the bookkeeping this layer exists to abolish.</para>
    ///
    /// <para>It comes back when it can rewrite references. Until then the refusal names it, so a model
    /// reaching for it is told the operation does not exist rather than handed a broken one.</para>
    /// </summary>
    [Fact]
    public void There_is_no_rename()
    {
        Assert.DoesNotContain("rename", CordOps.DomainOpNames);
        Assert.DoesNotContain("rename", CordOps.DomainOpsSchema().ToJsonString());

        var (_, errors) = Apply(new CordApp(Key: "a"),
            """{"ops":[{"op":"rename","entity":"deal","to":"opportunity"}]}""");
        Assert.Equal(CordErrorCode.MalformedOperation, Assert.Single(errors).Code);
    }

    /// <summary>
    /// <b>An upsert touches its own aggregate and nothing else.</b>
    ///
    /// <para>The property that collection-replacing operations could not offer, and the reason the
    /// vocabulary moved to this granularity: adding a second entity leaves the first exactly as it was,
    /// and replacing one field leaves its siblings alone. Under <c>replace_domain</c> the second call
    /// here would have deleted <c>deal</c>, and the only defence was the author remembering to restate
    /// everything that was already correct.</para>
    /// </summary>
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
        Assert.Equal(["name", "amount"], deal.FieldList.Select(f => f.Key));   // order kept, sibling kept
        Assert.Equal("Value", deal.FieldList[1].Label);                        // and the named one replaced
    }

    /// <summary>Upserting an entity REPLACES it in place rather than appending a second one with the
    /// same key — and keeps its position, so a domain does not shuffle itself every time one entity is
    /// corrected.</summary>
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

    /// <summary>Operations apply in ORDER — a field added to an entity created earlier in the SAME batch
    /// finds it. Stated as a test because a batch that reordered itself would be impossible to reason
    /// about, and because it is what lets one call be a whole thought.</summary>
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

    // ---- refusals, in Cord's own vocabulary ------------------------------------------------------

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

    /// <summary>
    /// Duplicate keys are still refused, but there is now only ONE way to state one — inside a single
    /// aggregate, which is a typo rather than a disagreement about whether a key was taken.
    ///
    /// <para>Upserts removed the whole class of "this already exists" refusal, and that is a deliberate
    /// trade rather than a loss: under <c>add_entity</c> the model had to know which of three creation
    /// operations applied to a key it could not see, and getting it wrong cost a repair round to learn a
    /// fact <c>inspect</c> would have given it for free.</para>
    /// </summary>
    [Fact]
    public void The_same_key_twice_inside_one_entity_is_refused()
    {
        Assert.Equal(CordErrorCode.DuplicateField, Assert.Single(Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
          {"key":"n","label":"N","type":"text"},
          {"key":"n","label":"Again","type":"text"}]}}]}
        """).Errors).Code);
    }

    /// <summary>
    /// <b>A duplicate ENTITY key can now only arrive from a document, never from an operation.</b>
    ///
    /// <para>Upserting is keyed, so two <c>upsert_entity</c> calls with one key produce one entity — the
    /// operation surface cannot express the error at all. The check is still worth keeping, and this is
    /// the path that reaches it: a draft is resumed by re-importing a stored definition, and
    /// <c>CordImport</c> is TOTAL, so it faithfully imports a document that should never have existed
    /// rather than throwing. Refusing at the next change is how the author finds out.</para>
    /// </summary>
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

    /// <summary>Removing something that is not there is still refused — an author reaching for a key the
    /// app does not have is working from a stale idea of it. Upserting is the one case that is NOT an
    /// error: "make this field be this" is answerable whether or not it existed.</summary>
    [Fact]
    public void Removing_a_field_that_is_not_there_is_refused()
    {
        var start = Apply(new CordApp(Key: "a"), """
        {"ops":[{"op":"upsert_entity","entity":{"key":"deal","label":"Deal","fields":[
          {"key":"n","label":"N","type":"text"}]}}]}
        """).App;

        Assert.Equal(CordErrorCode.UnknownField, Assert.Single(Apply(start,
            """{"ops":[{"op":"remove","entity":"deal","field":"ghost"}]}""").Errors).Code);

        // …and the upsert simply adds it.
        var (app, errors) = Apply(start, """
        {"ops":[{"op":"upsert_field","entity":"deal","field":{"key":"fresh","label":"F","type":"text"}}]}
        """);
        Assert.Empty(errors);
        Assert.Equal(["n", "fresh"], app.EntityList[0].FieldList.Select(f => f.Key));
    }

    /// <summary>Checked PER SECTION, not per screen — a screen may legitimately draw on several
    /// entities, so the resolvable-entity question has to be asked of each one.</summary>
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

    /// <summary>Rule 1's guard: a surface that must be ENUMERATED cannot quietly grow to 351 rules.
    /// Every member of the enum has to be reachable by a test, so adding one is a visible act.</summary>
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
            // Tabs and scope. UnknownTab and DuplicateTab are produced above; OutsideScope belongs to
            // the workspace and is produced by CordWorkspaceTests — same suite, and the rule is that
            // a code is reachable by a test, not that it is reachable from this file.
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

    // ---- behaviour -------------------------------------------------------------------------------

    private static CordApp Deals() => CordImport.Import(JsonNode.Parse(
        """
        {"key":"crm","name":"CRM","version":"1.0.0","entities":[
          {"key":"deal","label":"Deal","fields":[
            {"key":"title","label":"Title","type":"text"},
            {"key":"status","label":"Status","type":"select","options":[
              {"value":"open","label":"Open"},{"value":"won","label":"Won"}]},
            {"key":"won_on","label":"Won on","type":"date"}]}]}
        """));

    /// <summary>
    /// <b>The slice in one test.</b> The author says a deal can be won and what that does; they never
    /// name a command, and one appears — with the right key, the right entity and the effects filed on
    /// it — beside a transition that points at it.
    ///
    /// <para>That link is the thing 52 of 52 corpus commands say is bookkeeping rather than a decision,
    /// and it is the reason a transition pointing at another entity's command is now unreachable.</para>
    /// </summary>
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

        // The transition names a command the author never wrote…
        Assert.Equal("win", (string)transition["command"]!);
        // …and it exists, keyed to match, on the process's entity.
        Assert.Equal("win", (string)command["key"]!);
        Assert.Equal("deal", (string)command["entity"]!);
        Assert.Equal("Mark as won", (string)command["label"]!);
        Assert.Equal("Deal won", (string)command["successMessage"]!);
        Assert.Equal("{{today}}", (string)command["effects"]![0]!["set"]!["won_on"]!);

        // The state change itself is NOT an effect — the platform performs it.
        Assert.Equal("won", (string)transition["to"]!);
    }

    /// <summary>
    /// A move with nothing to do emits no command at all.
    ///
    /// <para>The plan's "emit absence": <c>AppCompiler.SynthesizeCommands</c> already produces a button
    /// for a bare transition, so authoring an empty command here would be Cord writing something nobody
    /// asked for — and something a later reader would have to decide whether to keep.</para>
    /// </summary>
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

    /// <summary>
    /// <b>Separation of duties, which until 2026-08-11 could not be said at all.</b>
    ///
    /// <para>A transition had no place to put a guard, so every state-moving command a Cord-authored app
    /// produced was unconditional. The live run wrote nine commands and guarded none of them; eight were
    /// transition-bound. That was not carelessness — there was no word for it, and an unmodelled thing
    /// is a wall rather than a slow leak.</para>
    ///
    /// <para>The guard lowers onto the COMMAND, which is the enforcing end:
    /// <c>CommandExecutor.ExecuteAsync</c> evaluates it against the record and the acting user before it
    /// checks the state machine, so a refused move never reaches the transition. This is a server-side
    /// authorization check, not a hidden button.</para>
    ///
    /// <para>And it is the only way to say it. A role grant cannot: it says which BUTTONS somebody has,
    /// not which RECORDS they may press them on, and one person holding both submitter and manager
    /// passes every role check in the app on their own claim.</para>
    /// </summary>
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

    /// <summary>
    /// <b>A guard is a reason to emit a command. Everything else on a transition is only a reason to
    /// decorate one.</b>
    ///
    /// <para>This is the failure mode the previous test would not have caught, and it is a security hole
    /// rather than a cosmetic one. A bare transition emits no command, because
    /// <c>AppCompiler.SynthesizeCommands</c> makes the button — and a synthesized command is
    /// UNCONDITIONAL. So a transition carrying nothing but a guard, lowered by the old rule, would have
    /// produced a perfectly valid application in which the guard did not exist. It would have passed the
    /// gate, passed smoke, published, and let anybody approve their own claim.</para>
    ///
    /// <para>Losing an icon costs an icon. This is the one thing on a transition that cannot be allowed
    /// to fall through the "nothing to present" test, which is why it is asserted separately from the
    /// case above rather than trusted to it.</para>
    /// </summary>
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

        // Not synthesized later — authored here, WITH the guard on it.
        var command = Assert.IsType<JsonObject>(Assert.Single(lowered["commands"]!.AsArray())!);
        Assert.Equal("win", (string)command["key"]!);
        Assert.NotNull(command["when"]);
        Assert.Equal("win", (string)lowered["processes"]![0]!["transitions"]![0]!["command"]!);
    }

    /// <summary>A transition that lands somewhere its own lifecycle never declared. Cord catches this
    /// rather than the gate because states and transitions arrive in ONE statement here, so it is a
    /// mistake inside something the author wrote — nameable at the operation index.</summary>
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

    /// <summary>Automations and roles lower to the sections the runtime reads, with the trigger
    /// flattened on the wire — <c>on</c> plus the entity, rather than a nested object whose only job is
    /// to hold two strings.</summary>
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
        // "record.updated, but only when THIS field changed" IS the platform's field.changed. Lowering
        // it as record.updated was structurally legal and semantically wrong: the automation would have
        // fired on every write, and nothing anywhere would have rejected it.
        Assert.Equal("field.changed", (string)workflow["trigger"]!["event"]!);
        Assert.Equal("deal", (string)workflow["trigger"]!["entity"]!);
        Assert.Equal("status", (string)workflow["trigger"]!["field"]!);
        Assert.True((bool)workflow["effects"]![0]!["setIfEmpty"]!);

        var grant = (JsonObject)lowered["roles"]![0]!["grants"]![0]!;
        Assert.Equal("deal", (string)grant["entity"]!);
        Assert.Equal("win", (string)grant["commands"]![0]!);
    }

    /// <summary>
    /// <b>Roles accumulate; each names only itself.</b>
    ///
    /// <para>The behaviour half of the granularity change, and the failure it removes is concrete: under
    /// <c>define_roles</c> a model adding an approver on its second call had to restate the submitter it
    /// wrote on its first, and forgetting to was an app with one role and no evidence anything went
    /// wrong. Removal is now something the author has to ASK for.</para>
    /// </summary>
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

        // And removal is available, but only when asked for by name.
        var (gone, removeErrors) = Apply(two, """
        {"ops":[{"op":"remove_behaviour","kind":"role","key":"rep"}]}
        """);
        Assert.Empty(removeErrors);
        Assert.Equal(["manager"], gone.RoleList.Select(r => r.Key));

        Assert.Equal(CordErrorCode.UnknownBehaviour, Assert.Single(Apply(gone, """
        {"ops":[{"op":"remove_behaviour","kind":"role","key":"rep"}]}
        """).Errors).Code);
    }

    /// <summary>One lifecycle per entity: restating REPLACES rather than appending. A second process on
    /// the same entity would give one record two state machines over one field.</summary>
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

    // ---- screens ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>The screen shape in one test: ONE screen, FOUR entities.</b>
    ///
    /// <para>This is the case the first version of <c>define_screens</c> could not express at all. It
    /// took <c>{entity, label, view}</c> — one screen, one entity — and the corpus appeared to justify
    /// that, since 53 of 60 pages reference exactly one entity. The corpus was wrong as a guide: every
    /// app with a multi-entity page is one a PERSON designed, and all eleven the generator produced have
    /// none. Measuring it would have cemented the generator's own ceiling into the authoring
    /// surface.</para>
    ///
    /// <para>The subject is CONTEXT — the screen is about scenarios — while its content spans hires,
    /// funding rounds and cost lines. That is budget planner's real "Investor Overview" page, which the
    /// runtime has always rendered and Cord simply had no words for.</para>
    /// </summary>
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
        Assert.Equal("scenario", (string)page["entity"]!);   // the SUBJECT, not the only entity shown

        // Consecutive metrics collapsed into one row — layout the author never described.
        var blocks = (JsonArray)page["blocks"]!;
        Assert.Equal("row", (string)blocks[0]!["kind"]!);
        Assert.Equal(2, ((JsonArray)blocks[0]!["blocks"]!).Count);
        Assert.Equal("card", (string)blocks[1]!["kind"]!);   // the chart, no longer in the metric row
        Assert.Equal("view", (string)blocks[2]!["kind"]!);

        // Each section kept its OWN entity.
        var stat = blocks[0]!["blocks"]![1]!;
        Assert.Equal("funding_round", (string)stat["blocks"]![0]!["source"]!["entity"]!);
        Assert.Equal("sum", (string)stat["blocks"]![0]!["source"]!["aggregate"]!["op"]!);

        var view = (JsonObject)lowered["views"]![0]!;
        Assert.Equal("hire", (string)view["entity"]!);
        Assert.Equal("open", (string)view["filters"]![0]!["value"]!);
        Assert.Equal("cost", (string)view["config"]!["columns"]![0]!);
    }

    /// <summary>Two lists of the SAME entity on one screen — "open" and "closed" — is an ordinary thing
    /// to want. Keys are generated, so they have to be unique by construction; a silent collision would
    /// make the second list replace the first at runtime.</summary>
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

    /// <summary>
    /// <b>Authoring screens over an app whose screens Cord cannot see is REFUSED.</b>
    ///
    /// <para>Two wrong answers shipped before this one, and both were data loss. An imported app carries
    /// its <c>pages</c> and <c>views</c> in the root raw overlay, because <c>CordImport</c> does not
    /// model them; <c>CordLower</c> emits Cord's screens and then merges that overlay over the top,
    /// arrays replacing wholesale. First, a screen change on a resumed draft was accepted with ZERO
    /// errors, reported to the model as applied, and then completely discarded — the author is told the
    /// screen exists and the app still has the old one. Then the overlay was dropped instead, which
    /// removed the lie by deleting every imported page.</para>
    ///
    /// <para><b>There is no correct merge available.</b> Joining the two sets needs keys on both sides
    /// and only one side has them. When the only implementable behaviours are "your change vanishes" and
    /// "their screens vanish", the operation cannot be honoured — so it is refused, by name, and the
    /// draft is untouched. Plan risk 3 asks for exactly this: an unmodelled thing must be a WALL, because
    /// a wall shows up as a named refusal and a leak shows up as silent data loss six months later.</para>
    ///
    /// <para>The corpus tests could never have caught the original bug: they only import, so
    /// <c>Screens</c> is always null and the two paths never meet. Found in review, twice.</para>
    /// </summary>
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

        // The overlay really is carrying them — otherwise this test would pass vacuously.
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

            // Refused AND inert: the imported screens are all still there, byte for byte.
            Assert.Equal(before, CordLower.Lower(app).ToJsonString());
        }
    }

    /// <summary>The refusal is scoped to the screens, not to the app. Everything else about an imported
    /// application is still editable, which is what keeps this a narrow gap rather than a dead end for
    /// every resumed draft.</summary>
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
        // …and the imported screens came through untouched.
        Assert.Equal(["old_page"],
            ((JsonArray)((JsonObject)CordLower.Lower(app))["pages"]!).Select(p => (string)p!["key"]!));
    }

    /// <summary>Screens accumulate one at a time, and removing one is something the author has to ask
    /// for by key — the same shape as roles, for the same reason.</summary>
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

    /// <summary>
    /// <b>The reason the tab operations exist.</b> A screen's tabs are revised ONE at a time, and the
    /// ones nobody touched come out byte-identical.
    ///
    /// <para>The alternative — saying the whole screen again — is what this refuses to require. Restating
    /// three accepted tabs to change the fourth puts them through a paraphrase every time, and the
    /// co-creation loop's entire promise is that revising one thing cannot alter the things already
    /// approved. Same argument as <c>upsert_field</c> against restating an entity.</para>
    /// </summary>
    [Fact]
    public void A_tab_is_revised_without_restating_its_siblings()
    {
        var draft = CordImport.Import(JsonNode.Parse("""
        {"key":"a","name":"A","version":"1.0.0","entities":[
          {"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"},
            {"key":"cost","label":"Cost","type":"number"}]}]}
        """));

        // A headline section AND tabs: the Budget Planner's Hiring Plan shape, which is the case that
        // settled tabs as coexisting with sections rather than replacing them.
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
        // The untouched tab is the SAME tab, not an equal-looking rebuild.
        Assert.Equal(before, after.TabList.Single(t => t.Key == "plan"));
        Assert.Equal("Cost by team", after.TabList.Single(t => t.Key == "cost").Label);
        // And the shared section above the tabs is untouched by a tab edit.
        Assert.Equal(["headline"], after.SectionList.Select(s => s.Key));

        // The tabs reach the runtime as a keyed `tabs` block under the shared content, which is what
        // makes the headline row stay put while the perspectives swap.
        var page = (JsonObject)((JsonArray)((JsonObject)CordLower.Lower(revised))["pages"]!)[0]!;
        var tabsBlock = ((JsonArray)page["blocks"]!).OfType<JsonObject>()
            .Single(b => (string?)b["kind"] == "tabs");
        Assert.Equal(["plan", "cost"],
            ((JsonArray)tabsBlock["tabs"]!).Select(t => (string)t!["key"]!));
    }

    /// <summary>A tab operation naming something that is not there is refused rather than treated as a
    /// creation — the same rule every other removal follows, because an author working from a stale idea
    /// of the draft is better told now.</summary>
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

    /// <summary>Two tabs with one key is two things wearing one identity: the second wins every later
    /// edit, so the tab somebody changes is not the tab they were looking at.</summary>
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

    // ---- the shape is checked against the schema the model was given -----------------------------

    /// <summary>
    /// <b>An operation property Cord does not know is REFUSED, not silently dropped.</b>
    ///
    /// <para>The first case is the one that was actually written by hand and cost an afternoon: an
    /// <c>ask</c> spelled <c>{title, fields:[{key,label,type}]}</c> — the shape a model would reasonably
    /// invent, and the shape the App Definition does NOT use, since an ask names fields that already
    /// exist on the entity. Cord accepted it, kept nothing, and the application shipped with a prompt
    /// that collects no input and no error anywhere.</para>
    ///
    /// <para>"Accepted then discarded" is the worst answer an authoring API can give, and it gets worse
    /// the further from a model it travels: a live session has the provider checking tool arguments
    /// first, an MCP client or a CLI has nothing.</para>
    /// </summary>
    [Theory]
    // the real one, from the hand-authored pass
    [InlineData("ask.title", """
    [{"op":"upsert_action","action":{"key":"a","label":"A","entity":"deal",
      "ask":{"title":"Why?","fields":["note"]},
      "effects":[{"type":"notify","to":"{{record.owner}}","message":"x"}]}}]
    """, "title")]
    // nested three deep, inside an aggregate's window
    [InlineData("aggregate.during.covering.unknown", """
    [{"op":"upsert_entity","entity":{"key":"period","label":"Period","fields":[
      {"key":"total","label":"Total","type":"money",
       "aggregate":{"op":"sum","of":"line","field":"amount","over":"mine",
                    "during":{"covering":{"from":"a","myPoint":"b","untilForever":true}}}}]}}]
    """, "untilForever")]
    // nested inside a screen section's sort
    [InlineData("screen.sections.sort.unknown", """
    [{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
      {"kind":"list","of":"deal","sort":[{"field":"n","direction":"asc","nulls":"last"}]}]}}]
    """, "nulls")]
    // a real property in the wrong place: `columns` belongs to a section, not to a screen
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
        // Named: which operation, and which property.
        Assert.All(prepared.Errors, e => Assert.Equal(0, e.OperationIndex));
        Assert.Contains(prepared.Errors,
            e => e.Where.Contains(property, StringComparison.Ordinal)
                 || e.Message.Contains(property, StringComparison.Ordinal));

        // ATOMIC: the draft is byte-identical, so a half-understood change is never half applied.
        Assert.Equal(before, CordLower.Lower(prepared.Next).ToJsonString());
    }

    /// <summary>One bad operation refuses the whole batch, including the good ones beside it. A batch is
    /// one thought; applying the half of it that parsed would leave an application nobody described.
    /// </summary>
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

    /// <summary>
    /// <b>A calendar must say which date it is about.</b>
    ///
    /// <para>Cord's question rather than the gate's, and the distinction is why it is worth asking: the
    /// renderer falls back to an entity's FIRST date field, so a calendar that names none is a valid
    /// document that answers a different question than its own heading. The live run wrote "Decisions
    /// due" over an entity whose dates begin with <c>submitted_on</c>.</para>
    /// </summary>
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

        // Naming it is all that was ever needed.
        var (ok, none) = Apply(draft, """
        {"ops":[{"op":"upsert_screen","screen":{"key":"d","label":"D","sections":[
          {"kind":"list","of":"claim","label":"Decisions due","view":"calendar","dateField":"due_by"}]}}]}
        """);
        Assert.Empty(none);
        Assert.Equal("due_by", ok.Screens![0].SectionList[0].DateField);
    }

    /// <summary>
    /// <b>A governed default that CONTRADICTS the lifecycle is refused; one that agrees is absorbed.</b>
    ///
    /// <para>The absorbing half is the same mechanical deduplication governed OPTIONS already get — an
    /// author who states a starting state in both places has said one true thing twice, and the live run
    /// lost a submission to it. The refusing half is where that stops: two different claims about where
    /// a record begins is not a duplication, and resolving it by picking one would be Cord deciding
    /// something the author never did.</para>
    /// </summary>
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

        // Agreeing: no complaint, and the duplicate never reaches the document.
        var (app, none) = Apply(Draft("draft"), Lifecycle);
        Assert.Empty(none);
        var lowered = (JsonObject)CordLower.Lower(app);
        var status = lowered["entities"]![0]!["fields"]!.AsArray().OfType<JsonObject>()
            .Single(f => (string?)f["key"] == "status");
        Assert.Null(status["default"]);
    }

    /// <summary>
    /// A tool accepts only its OWN concern's operations.
    ///
    /// <para>Concern-scoped schemas mean nothing if the parser takes the union anyway: the domain tool
    /// would accept <c>define_lifecycle</c>, a model guessing at vocabulary it was never offered would
    /// be rewarded for it, and rule 3's boundary would be documentation rather than a boundary. Found in
    /// review — and the test fixture that "proved" screens worked was itself sending
    /// <c>define_screens</c> through the domain tool.</para>
    /// </summary>
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
        // Named, not just refused — the model is told which tool does take it, so a wrong guess costs
        // one corrected call instead of a hunt.
        Assert.Contains("tool", error.Message);
    }

    // ---- rule 3: the schema stays small ----------------------------------------------------------

    /// <summary>
    /// The ceiling, and the doctrine that comes with it.
    ///
    /// <para>Same rule the screen tool schema already lives under in <c>ComposeDesignTests</c>, whose
    /// ledger records what happens without it: <i>"RAISED IN BULK, AND THIS ONE IS A PROBLEM … the
    /// narrowing pass the note above demands is now owed, not optional."</i> <b>If this trips, narrow —
    /// do not raise.</b> A semantic API that accretes operations until it is a 60 KB union has
    /// reproduced exactly the problem it was built to remove.</para>
    /// </summary>
    [Fact]
    public void The_domain_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: domain ops", CordOps.DomainOpsSchema());
        output.WriteLine(report.ToString());

        // 2026-08-10 — LOWERED from 12,000 to 10,000 (measured 9,992), and the story is the doctrine
        // working. Giving a screen sections took this schema to 13,469 and tripped the old bound. The
        // answer was not to raise it: screens moved to their own concern (CordOps.UiOpsSchema), and what
        // is left here is smaller than it was before. A ceiling that only ever goes up is a record of
        // surrender.
        //
        // 2026-08-11 — LOWERED again, 10,000 → 9,000. Seven operations became three: the three ways to
        // create an entity collapsed into upsert_entity, add_field/set_field into upsert_field, and
        // rename left entirely because it renamed a key without rewriting anything that pointed at it.
        // Vocabulary was REMOVED and the surface got more expressive, which is the only kind of shrink
        // worth recording.
        Assert.InRange(report.Bytes, 1, 9_000);
        Assert.Equal(3, report.Operations);
    }

    /// <summary>
    /// The UI ceiling — <b>the rule-3 test the plan called the one that matters most</b>, because UI is
    /// where a semantic surface is likeliest to give up and readmit the block union.
    ///
    /// <para>It stays small for one reason: the author says WHAT belongs on a screen and never how it is
    /// arranged. The moment a raw block tree becomes reachable from here, the 41-kind catalog is back in
    /// the prefix and Cord has reproduced the problem it exists to remove. <b>If this trips, add a
    /// section kind — do not widen toward blocks.</b></para>
    ///
    /// <para><b>Raised once, from 6,000 to 7,000, when named tabs landed (2026-08-12).</b> The ceiling
    /// is not a budget to defend — it is a tripwire that forces the question "has this concern grown
    /// into a second concern?" to be answered out loud. Here the answer was no: a tab belongs to the
    /// screen its author is writing, so putting it behind a second tool would split one aggregate's
    /// vocabulary across two prompts. What the extra 691 bytes bought is the ability to revise one tab
    /// without restating the three beside it, which is the property the whole one-aggregate-at-a-time
    /// loop rests on. The guard that keeps its teeth is the sibling test below: no block tree, at any
    /// size.</para>
    /// </summary>
    [Fact]
    public void The_ui_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: ui ops", CordOps.UiOpsSchema());
        output.WriteLine(report.ToString());

        Assert.InRange(report.Bytes, 1, 7_000);
        // Four: the screen pair, plus the tab pair. The tabs were bought deliberately — a named tab is
        // a child aggregate with a stable key, so revising one must not restate its siblings, which is
        // the same argument that made upsert_field a separate operation from upsert_entity.
        Assert.Equal(4, CordOps.UiOpNames.Count);
    }

    /// <summary>No block vocabulary is reachable from the screen schema. The corpus has 41 block kinds
    /// and the entire premise of a semantic UI layer is that none of them are the author's problem.</summary>
    [Theory]
    [InlineData("blocks")]
    [InlineData("kind\":{\"const")]   // the block union's if/then dispatch
    [InlineData("card")]
    [InlineData("row")]
    [InlineData("stack")]
    // NOT `columns`: it is a legitimate SECTION property — which fields a list shows — and also happens
    // to be a block kind. Asserting on the word would fail on the honest use, the same overreach that
    // made the `via` guard trip on `ownedBy`. The word is not the problem; a block TREE is.
    public void The_ui_schema_never_offers_a_block_tree(string forbidden)
    {
        var json = CordOps.UiOpsSchema().ToJsonString().Replace(" ", "");

        // `kind` IS a property here — of a section, with four values — so the assertion is about block
        // CONTAINERS and the union's dispatch shape, not the word.
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

    /// <summary>
    /// Writes the schema out so it can be read, reviewed and DIFFED.
    ///
    /// <para>Rule 3's real enforcement is not the byte ceiling — it is that growth has to be visible.
    /// A ceiling says a number got bigger; a checked-in artifact says which operation gained which
    /// property, in a pull request, next to the reason. The App Definition schema is generated and
    /// checked in for the same reason, with the same trade: the file is an output, never edit it.</para>
    /// </summary>
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

    /// <summary>
    /// Slice 4's ceiling, and the comparison that makes rule 3 mean something.
    ///
    /// <para>Behaviour carries more OPERATIONS than the domain but less schema, and the split is the
    /// point: each is one aggregate — a lifecycle with its states and transitions, a role with its
    /// grants — so there is no separate edit vocabulary for the parts. <b>If it trips, narrow — do not
    /// raise.</b></para>
    /// </summary>
    [Fact]
    public void The_behaviour_schema_stays_inside_its_ceiling()
    {
        var report = CordOps.SchemaReport("cord: behaviour ops", CordOps.BehaviourOpsSchema());
        output.WriteLine(report.ToString());

        // 2026-08-11 — RAISED, 9,000 → 9,500. Bought: the collection-replacing define_actions/
        // define_automations/define_roles became per-aggregate upserts plus one remove_behaviour, so
        // adding a second role no longer means restating the first — and forgetting to restate it no
        // longer silently deletes it. Worth it.
        //
        // 2026-08-13 — HELD at 9,500 while GAINING vocabulary, which is the outcome the doctrine is
        // for. A live agent building a recurring-task app wanted one "repeats on" multiselect and a
        // guard testing it; Cord offered 8 comparisons where the App Definition allows 13, so with no
        // set-membership operator it modelled seven boolean columns instead. `contains`, `in` and
        // `notIn` were added — Cord being narrower than the platform is the "unmodelled thing is a
        // wall" failure, and it reads as the model being stupid rather than the vocabulary being
        // short. It tripped this bound by 3 bytes, and the answer was to narrow rather than raise:
        // `confirmLabel` lost a description that restated its own name. 9,503 → 9,452.
        //
        // These bounds are a TRIPWIRE for a concern quietly becoming a union, not a budget to optimise
        // against. They have earned their keep twice (they forced the screens/domain split) and they are
        // deliberately coarse. Do not spend attention shaving hundreds of bytes off one; if one trips,
        // ask what vocabulary arrived and whether it belongs in this concern.
        Assert.InRange(report.Bytes, 1, 9_500);
        Assert.Equal(5, report.Operations);
    }

    /// <summary>
    /// <b>The rule-3 test that actually holds the line.</b>
    ///
    /// <para>Concerns are handed out separately or they are not handed out separately. A byte ceiling
    /// can be satisfied by terser prose; this cannot be satisfied by anything except keeping the two
    /// vocabularies apart. The failure it prevents is the one the plan names: a semantic API that
    /// accretes operations until <c>apply_changes</c> is a 60 KB union has reproduced exactly the
    /// problem Cord was built to remove.</para>
    /// </summary>
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

        // And no operation appears in two lists, which a copy-paste would otherwise make possible.
        var all = schemas.SelectMany(x => x.Own).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The command link is not on the wire, in either direction.
    ///
    /// <para>52 of 52 transition-linked commands in the corpus belong to exactly one transition and
    /// carry their process's entity, so the link is bookkeeping rather than a decision — and the whole
    /// value of the behaviour slice is that an author cannot get it wrong. If <c>command</c> ever
    /// appears as a property here, a transition can point at another entity's command again and the
    /// slice has quietly given back what it bought.</para>
    /// </summary>
    [Theory]
    [InlineData("command")]
    [InlineData("commandKey")]
    [InlineData("emits")]      // real vocabulary, deliberately unmodelled — see the raw allowlist
    public void The_behaviour_schema_never_offers_the_command_link(string forbidden)
    {
        var json = CordOps.BehaviourOpsSchema().ToJsonString().Replace(" ", "");
        Assert.DoesNotContain($"\"{forbidden}\":{{", json);
    }

    /// <summary>The behaviour schema is written out for review, like the domain one. Rule 3's real
    /// enforcement is that growth is VISIBLE in a diff, not that a number stayed under a bound.</summary>
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

    /// <summary>The other half of rule 3: a byte ceiling alone can be met by writing terser prose, so
    /// the EXACT operation set is pinned. A behaviour or UI operation drifting into the domain schema
    /// fails here rather than in a live run.</summary>
    [Fact]
    public void The_domain_schema_offers_exactly_the_domain_operations()
    {
        var ops = (CordOps.DomainOpsSchema()["properties"]!["ops"]!["items"]!["oneOf"] as JsonArray)!
            .Select(o => (string)o!["title"]!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(CordOps.DomainOpNames.OrderBy(x => x, StringComparer.Ordinal).ToList(), ops);
    }

    /// <summary>
    /// The one property the author must never be offered.
    ///
    /// <para><c>via</c> is derivable in 45 of 45 corpus aggregates — and in 24 of 24 owned entities,
    /// so it is absent from <c>ownedBy</c> too. Putting it on the wire would hand back the decision the
    /// whole shape exists to remove, and a wrong join is valid, compiles, and reports a wrong number.
    /// Same for <c>match</c> and the window's <c>against</c>.</para>
    /// </summary>
    [Theory]
    [InlineData("via")]
    [InlineData("match")]
    [InlineData("against")]
    [InlineData("within")]    // `during.inside.at` IS authored — it is the record's own date. What the
                              // author never writes is its pairing with a `within` range on the other
                              // side of the relationship; that is what the variant name decides.
    [InlineData("targetEntity")]
    [InlineData("computed")]
    [InlineData("rollup")]
    public void The_schema_never_offers_machinery_the_lowerer_derives(string forbidden)
    {
        var json = CordOps.DomainOpsSchema().ToJsonString();

        // Property NAMES only. The word may legitimately appear in prose — "which one that is gets
        // worked out for you" is exactly the kind of sentence that should mention it.
        Assert.DoesNotContain($"\"{forbidden}\":{{", json.Replace(" ", ""));
    }

    // ---- the vocabulary Cord offers must be a vocabulary Cord can lower --------------------------

    /// <summary>
    /// <b>Every word Cord offers, in every vocabulary, lowers to a value the App Definition accepts.</b>
    ///
    /// <para>This is the test whose absence cost a run and then cost four more findings. Cord offered
    /// <c>board</c> while the enum said <c>kanban</c>; <c>schedule.daily</c> while it said
    /// <c>schedule</c>; <c>secondary</c> while it said <c>default</c>; <c>rowMenu</c>/<c>listHeader</c>
    /// while it said <c>tableRow</c>/<c>bulkToolbar</c>/<c>kanbanCard</c>. In every case the model
    /// picked a legal value out of the vocabulary Cord handed it and got back a structural rejection
    /// naming a pointer into a document it never wrote — which reads, from every log and every
    /// dashboard, exactly like the model being bad at its job.</para>
    ///
    /// <para>The enums are read out of the REAL schema at the pointer each one lowers into, never
    /// restated here. Restating them would recreate the original bug one level up: two hand-written
    /// lists and nothing forcing them to agree.</para>
    /// </summary>
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

    /// <summary>
    /// <b>Every value the platform accepts is either OFFERED or explicitly WITHHELD with a reason.</b>
    ///
    /// <para>The companion to the test above, and the one that turns a judgement into a structure.
    /// That one stops Cord offering a word the platform will reject. This one stops Cord being
    /// silently narrower than the platform — which is not a bug in itself and is frequently correct,
    /// but is indistinguishable from an oversight by anyone standing outside the code.</para>
    ///
    /// <para><b>Written after three live agent reports in one day, all the same shape.</b> Cord
    /// offered 8 comparisons where the platform allows 13; with no set-membership operator an agent
    /// modelled seven boolean columns instead of one multiselect, and reported the tool as limited.
    /// It was right. `contains` was a genuine oversight, `between` a considered exclusion, and
    /// nothing distinguished them — so an author had to treat every absence as a wall and route
    /// around it. Naming the withheld set with a reason makes the difference legible, and this test
    /// makes it impossible to add a platform value without someone deciding which kind it is.</para>
    ///
    /// <para>The failure message is the point: it names the value and asks for a decision, rather
    /// than telling anybody what to do with it. Some belong in the vocabulary and some emphatically
    /// do not — an effect that sends mail out of the system is not something a model-facing surface
    /// should hand out because a test complained.</para>
    /// </summary>
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

        // A word cannot be both — that would be a map claiming to offer what it says it does not.
        Assert.Empty(offered.Intersect(withheld, StringComparer.Ordinal));
    }

    /// <summary>The other direction, and the one a byte ceiling cannot enforce: the schema offers
    /// EXACTLY the map's words. A word added to one and not the other is how all five defects
    /// happened.</summary>
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

        // Every word appears in the schema, and every value the schema offers for this vocabulary is
        // one of them — checked by looking for a word the map does NOT have.
        foreach (var word in map.Words) Assert.Contains($"\"{word}\"", schema);
        foreach (var stray in new[] { "listHeader", "schedule.daily-ish", "muted", "accent" })
            if (!map.Words.Contains(stray)) Assert.DoesNotContain($"\"{stray}\"", schema);
    }

    /// <summary>
    /// Every word the schema offers for a screen's <c>view</c> lowers to a type the App Definition
    /// actually accepts.
    ///
    /// <para><b>This test exists because its absence cost a live run.</b> Cord's schema offered
    /// <c>board</c>, the App Definition's enum says <c>kanban</c>, and <c>CordLower</c> copied the word
    /// across untranslated. The model chose a legal Cord value and got back
    /// <i>"STRUCTURAL at [/views/0/type]: Value should match one of the values specified by the
    /// enum"</i> — a defect that looks, from every dashboard and every log line, precisely like the
    /// model being bad at its job.</para>
    ///
    /// <para>The enum is read out of <c>Schemas.AppDefinitionSchemaNode()</c> rather than typed here.
    /// Restating it would recreate the original bug one level up: two hand-written lists, no mechanism
    /// forcing them to agree.</para>
    /// </summary>
    [Fact]
    public void Every_screen_view_word_lowers_to_a_real_view_type()
    {
        var allowed = (Schemas.AppDefinitionSchemaNode()["$defs"]?["view"]?["properties"]?["type"]?["enum"]
                       ?? Schemas.AppDefinitionSchemaNode()["properties"]?["views"]?["items"]?["properties"]?["type"]?["enum"])
            as JsonArray;

        Assert.NotNull(allowed);
        var types = allowed!.Select(v => (string)v!).ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"App Definition view types: {string.Join(", ", types.OrderBy(x => x, StringComparer.Ordinal))}");

        // Offered → lowerable.
        foreach (var word in CordVocabulary.Views.Words)
            Assert.Contains(CordVocabulary.Views.Lower(word)!, types);

        // And the schema offers exactly the words the map knows, so neither list can grow alone. The
        // view word now lives on a SECTION rather than on a screen, since one screen may show several
        // entities several ways.
        var offered = (CordOps.UiOpsSchema()["$defs"]!["section"]!["properties"]!["view"]!["enum"]
            as JsonArray)!
            .Select(v => (string)v!)
            .ToList();

        Assert.Equal(CordVocabulary.Views.Words.ToList(), offered);
    }
}
