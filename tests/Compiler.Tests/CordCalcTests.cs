// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The calculation grammar — the reason to believe the whole idea works.
///
/// <para>A rollup is the most mechanical thing in the App Definition and the place a model is most
/// likely to be valid-but-wrong. The author has to decide four things that are all about the schema
/// rather than the business: which reference joins the two records, whether it is a direct or a
/// sibling aggregation, which side of the relationship each date lives on, and which of two window
/// spellings to use. Three of those four disappear here, and the tests below are what say so.</para>
///
/// <para>If this does not hold, stop. Everything after it is a bet on a layer that could not carry the
/// one construct it was designed for.</para>
/// </summary>
public class CordCalcTests(ITestOutputHelper output)
{
    // ---- the six shapes, worked ------------------------------------------------------------------

    /// <summary>A budget scenario: periods and the rows that contribute to them. Enough structure for
    /// `via` to be inferable and for both window directions to be expressible.</summary>
    private static CordApp Model(params CordField[] periodFields) => new(
        Key: "budget",
        Entities:
        [
            new CordEntity("scenario", "Scenario", Fields: [new CordField("name", "Name", "text")]),
            new CordEntity("period", "Period", Fields:
            [
                new CordField("scenario", "Scenario", "reference", TargetEntity: "scenario"),
                new CordField("start_date", "Start", "date"),
                new CordField("end_date", "End", "date"),
                new CordField("sequence", "Order", "integer"),
                .. periodFields,
            ]),
            new CordEntity("hiring_line", "Hire", Fields:
            [
                new CordField("scenario", "Scenario", "reference", TargetEntity: "scenario"),
                new CordField("monthly_cost", "Cost", "money"),
                new CordField("start_month", "From", "date"),
                new CordField("end_month", "To", "date"),
            ]),
            new CordEntity("funding_round", "Round", Fields:
            [
                new CordField("scenario", "Scenario", "reference", TargetEntity: "scenario"),
                new CordField("amount", "Amount", "money"),
                new CordField("expected_close", "Closes", "date"),
            ]),
            new CordEntity("invoice", "Invoice", Fields: [new CordField("name", "Name", "text")]),
            new CordEntity("invoice_line", "Line", Fields:
            [
                new CordField("invoice", "Invoice", "reference", TargetEntity: "invoice"),
                new CordField("amount", "Amount", "money"),
            ]),
        ]);

    private static JsonNode Computed(CordApp app, string entity, string field)
    {
        var doc = (JsonObject)CordLower.Lower(app);
        var e = (doc["entities"] as JsonArray)!.OfType<JsonObject>().First(x => (string?)x["key"] == entity);
        var f = (e["fields"] as JsonArray)!.OfType<JsonObject>().First(x => (string?)x["key"] == field);
        return f["computed"]!;
    }

    /// <summary>
    /// SPANNING / DATE — the shape budget-planner's payroll is built from, and the one the schema
    /// spends seven hundred characters explaining.
    ///
    /// <para>Note what the author did NOT write: <c>via</c> (derived — hiring_line's only reference to
    /// scenario), and the fact that <c>from</c>/<c>to</c> live on the aggregated row while
    /// <c>against</c> lives on this one. <c>match</c> is implied by <c>over</c> naming a reference
    /// rather than <c>"mine"</c>.</para>
    /// </summary>
    [Fact]
    public void A_spanning_window_over_siblings_lowers_to_the_full_rollup_machinery()
    {
        var app = Model(new CordField("payroll_cost", "People cost", "money",
            Calc: new CordAggregate("sum", "hiring_line", "monthly_cost", "scenario",
                During: new CordCovering("start_month", "end_month", "start_date"))));

        Assert.Equal(
            """{"rollup":{"entity":"hiring_line","via":"scenario","match":"scenario","op":"sum","field":"monthly_cost","window":{"from":"start_month","to":"end_month","against":"start_date"}}}""",
            Computed(app, "period", "payroll_cost").ToJsonString());
    }

    /// <summary>
    /// SPANNING / NUMBER — <b>byte-for-byte the same Cord shape</b>, only the field names differ.
    ///
    /// <para>This is the single strongest argument for a structured aggregate over a string grammar. A
    /// string would have been tempted to spell a month index and a calendar date differently, and then
    /// "the months this hire covers" and "the first six periods" — the same question at two scales —
    /// would have needed two spellings and two sets of rules. Here the types decide, and the gate
    /// already enforces that a window compares like with like.</para>
    /// </summary>
    [Fact]
    public void A_numeric_window_needs_no_different_syntax_from_a_date_window()
    {
        var app = Model(new CordField("growth_pct", "Growth", "decimal",
            Calc: new CordAggregate("avg", "hiring_line", "monthly_cost", "scenario",
                During: new CordCovering("start_month", "end_month", "sequence"))));

        var window = Computed(app, "period", "growth_pct")["rollup"]!["window"]!;
        Assert.Equal("""{"from":"start_month","to":"end_month","against":"sequence"}""", window.ToJsonString());
    }

    /// <summary>DATED — a round landing inside the period it closes in. The other direction, and the
    /// variant name is the only thing that had to change.</summary>
    [Fact]
    public void A_dated_window_lowers_to_at_and_within()
    {
        var app = Model(new CordField("funding_in", "Funding", "money",
            Calc: new CordAggregate("sum", "funding_round", "amount", "scenario",
                During: new CordInside("expected_close", "start_date", "end_date"))));

        Assert.Equal(
            """{"at":"expected_close","within":{"from":"start_date","to":"end_date"}}""",
            Computed(app, "period", "funding_in")["rollup"]!["window"]!.ToJsonString());
    }

    /// <summary>An open bound stays open. An unfinished hire is still on the payroll — the absence has
    /// to survive lowering, not become an exclusion.</summary>
    [Fact]
    public void An_open_bound_lowers_as_absent_rather_than_as_a_limit()
    {
        var app = Model(new CordField("open_ended", "Open", "money",
            Calc: new CordAggregate("sum", "hiring_line", "monthly_cost", "scenario",
                During: new CordCovering("start_month", null, "start_date"))));

        var window = (JsonObject)Computed(app, "period", "open_ended")["rollup"]!["window"]!;
        Assert.False(window.ContainsKey("to"));
    }

    /// <summary>The common case — 33 of the corpus's 45 aggregates. A parent totalling its own
    /// children says so in one word and gets no <c>match</c> at all.</summary>
    [Fact]
    public void The_plain_parent_case_is_one_word()
    {
        var app = new CordApp(Key: "inv", Entities: Model().EntityList.ToList());
        app = app with
        {
            Entities = app.EntityList.Select(e => e.Key != "invoice" ? e : e with
            {
                Fields = [.. e.FieldList, new CordField("total", "Total", "money",
                    Calc: new CordAggregate("sum", "invoice_line", "amount", CordAggregate.Mine))],
            }).ToList(),
        };

        Assert.Equal(
            """{"rollup":{"entity":"invoice_line","via":"invoice","op":"sum","field":"amount"}}""",
            Computed(app, "invoice", "total").ToJsonString());
    }

    /// <summary>An expression is handed through untouched. <c>ComputedExpr</c> already owns this
    /// grammar, <c>prev()</c> included, and a second one here would be a regression.</summary>
    [Fact]
    public void An_expression_passes_through_verbatim()
    {
        const string expr = "prev(cash_end, scenario.starting_cash) + net_cash_movement";
        var app = Model(new CordField("cash_end", "Cash", "money", Calc: new CordExpr(expr)));

        var computed = (JsonObject)Computed(app, "period", "cash_end");

        // Compared as a VALUE, not as serialized text: the default encoder escapes `+` to +, so a
        // string comparison here would fail on an expression that round-trips perfectly. The hash
        // tests get this right for free by serializing both sides the same way.
        Assert.Equal(expr, (string?)computed["expr"]);
        Assert.False(computed.ContainsKey("rollup"));
    }

    // ---- inference, and its refusal --------------------------------------------------------------

    /// <summary>
    /// The census: <c>via</c> is uniquely derivable for EVERY aggregate in the corpus.
    ///
    /// <para>50 of 50 — measured, not hoped. That is what makes it right to take the property away
    /// from the author rather than merely default it: there was never a decision there, only a lookup
    /// they could get wrong. The floor is the exact count, not a percentage, so a regression names
    /// itself.</para>
    ///
    /// <para>Restated three times on 2026-08-19 as the budget planner's revenue model was rebuilt
    /// from the Liquiditaetsplan: 45/45 -> 56/56 -> 50/50. The count FELL at the end because the
    /// cohort engine replaced a wide blended-average model with a narrow one (60 exprs after the
    /// whole-customer rounding split active_customers in two) — a plan row stopped
    /// carrying a customer profile, and the weighted rollups that averaged them went with it. Every
    /// join still infers itself, which is the claim; the totals are only the tripwire.</para>
    /// </summary>
    [Fact]
    public void Every_aggregate_in_the_corpus_infers_its_own_join()
    {
        int aggregates = 0, inferred = 0, expressions = 0;
        var explicitVia = new List<string>();

        foreach (var path in Corpus.SemanticPaths())
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            var app = CordImport.Import(doc);
            foreach (var entity in app.EntityList)
                foreach (var field in entity.FieldList)
                    switch (field.Calc)
                    {
                        case CordExpr:
                            expressions++;
                            break;
                        case CordAggregate a:
                            aggregates++;
                            if (a.Via is null) inferred++;
                            else explicitVia.Add($"{app.Key}.{entity.Key}.{field.Key} (via={a.Via})");
                            break;
                    }
        }

        output.WriteLine($"computed fields: {expressions} expr + {aggregates} aggregate = {expressions + aggregates}");
        output.WriteLine($"via inferred:    {inferred}/{aggregates}");
        foreach (var e in explicitVia) output.WriteLine("  explicit: " + e);

        Assert.Equal(50, aggregates);
        Assert.Equal(60, expressions);
        Assert.Equal(50, inferred);
    }

    /// <summary>
    /// Two candidate references means no answer, and that is the correct behaviour.
    ///
    /// <para>Guessing would be worse than refusing: an aggregate joined through the wrong reference is
    /// structurally valid, compiles, and quietly reports a wrong number on a screen somebody trusts.
    /// The candidates are named so the author is asked a question they can actually answer.</para>
    /// </summary>
    [Fact]
    public void An_ambiguous_join_is_refused_rather_than_guessed()
    {
        var index = CordRefIndex.FromModel(
        [
            new CordEntity("invoice", "Invoice", Fields: [new CordField("n", "N", "text")]),
            new CordEntity("transfer", "Transfer", Fields:
            [
                new CordField("from_invoice", "From", "reference", TargetEntity: "invoice"),
                new CordField("to_invoice", "To", "reference", TargetEntity: "invoice"),
            ]),
        ]);

        Assert.Null(index.Infer("invoice", "transfer", CordAggregate.Mine));
        Assert.Equal(["from_invoice", "to_invoice"], index.Candidates("invoice", "transfer", CordAggregate.Mine));
    }

    /// <summary>A cross-app reference can never be the join between two records of THIS app, so it is
    /// not a candidate — which is also what stops it creating a phantom ambiguity.</summary>
    [Fact]
    public void A_cross_app_reference_is_not_a_candidate_join()
    {
        var index = CordRefIndex.FromModel(
        [
            new CordEntity("deal", "Deal", Fields: [new CordField("n", "N", "text")]),
            new CordEntity("activity", "Activity", Fields:
            [
                new CordField("deal", "Deal", "reference", TargetEntity: "deal"),
                new CordField("owner", "Owner", "reference", TargetEntity: "deal", TargetApp: "platform"),
            ]),
        ]);

        Assert.Equal("deal", index.Infer("deal", "activity", CordAggregate.Mine));
    }

    /// <summary>
    /// A <c>via</c> that inference would NOT reproduce is kept explicitly.
    ///
    /// <para>Totality outranks elegance. If a real document joins through the reference we would not
    /// have picked, the round-trip must still be exact — and the coverage report must not claim an
    /// inference that never happened. Both are satisfied by recording it.</para>
    /// </summary>
    [Fact]
    public void A_join_inference_would_not_reproduce_is_preserved_verbatim()
    {
        var doc = JsonNode.Parse("""
        {"key":"a","entities":[
          {"key":"invoice","label":"I","fields":[
            {"key":"total","label":"T","type":"money","computed":{"rollup":{"entity":"transfer","via":"to_invoice","op":"sum","field":"amount"}}}]},
          {"key":"transfer","label":"T","fields":[
            {"key":"from_invoice","label":"F","type":"reference","targetEntity":"invoice"},
            {"key":"to_invoice","label":"T","type":"reference","targetEntity":"invoice"},
            {"key":"amount","label":"A","type":"money"}]}]}
        """)!;

        var app = CordImport.Import(doc);
        var agg = (CordAggregate)app.EntityList[0].FieldList[0].Calc!;
        Assert.Equal("to_invoice", agg.Via);
        Assert.Equal(DefinitionHash.Of(doc), DefinitionHash.Of(CordLower.Lower(app)));
    }

    // ---- how much of the domain this actually models ---------------------------------------------

    /// <summary>
    /// The floor for the slice that owns entities.
    ///
    /// <para>Measured against the entity subtree with <c>detail</c>, <c>peek</c> and <c>form</c>
    /// excluded — those are authored block trees, they belong to the UI slice, and counting them here
    /// would set a floor that no amount of domain work could move. They are NOT hidden: they still
    /// count against the headline corpus number, and every one of them is listed as a raw pointer.</para>
    /// </summary>
    [Fact]
    public void The_domain_half_of_every_entity_is_modelled()
    {
        int total = 0, raw = 0;
        foreach (var path in Corpus.SemanticPaths())
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
            AppSchemaVersion.Stamp(doc);

            var (t, r) = CordCoverage.Section(
                CordLower.Lowering(CordImport.Import(doc)), "/entities", "detail", "peek", "form");
            total += t;
            raw += r;
        }

        var fraction = 1.0 - (double)raw / total;
        output.WriteLine($"entity domain coverage: {fraction * 100:F2}%  ({total - raw}/{total} nodes)");

        // 0.99, not 1.0: the corpus holds six genuine constructs this slice does not model —
        // four `mapsTo` and two `treeAggregate` — and each is a named line in the raw allowlist
        // rather than an unexplained shortfall.
        Assert.True(fraction >= 0.99,
            $"entity domain coverage {fraction * 100:F2}% is below the floor of 99.00%");
    }
}
