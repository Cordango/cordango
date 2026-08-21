// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The gate on a rollup <c>window</c>, which now comes in two directions.
///
/// <para><b>Spanning rows</b> — <c>from</c>/<c>to</c>/<c>against</c> — keep rows whose own range covers
/// a date on this record: a hire being paid this month. <b>Dated rows</b> — <c>at</c>/<c>within</c> —
/// keep rows whose own date falls inside a range on this record: a round closing this month. They ask
/// opposite questions, and a window that mixed them would be read one way and authored the other.</para>
///
/// <para>Held tightly because the failure mode is silence. A window that resolves nothing aggregates
/// nothing, and a plan showing €0 where it should show €500,000 looks exactly like a plan whose costs
/// have not been entered yet — which is how a €500,000 round sat invisible in a budget planner
/// (live 2026-08-05).</para>
/// </summary>
public class RollupWindowGateTests
{
    /// <summary>A scenario with two sibling children: hires that span months, fees paid on a day.</summary>
    private static JsonObject Doc(string window) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "plan", "name": "Plan", "version": "1.0.0",
      "entities": [
        { "key": "scenario", "label": "Scenario", "displayField": "name",
          "fields": [ { "key": "name", "label": "Name", "type": "text" } ] },
        { "key": "hire", "label": "Hire", "displayField": "role",
          "fields": [
            { "key": "role", "label": "Role", "type": "text" },
            { "key": "scenario", "label": "Scenario", "type": "reference", "targetEntity": "scenario" },
            { "key": "cost", "label": "Cost", "type": "money" },
            { "key": "starts", "label": "Starts", "type": "date" },
            { "key": "ends", "label": "Ends", "type": "date" }
          ] },
        { "key": "period", "label": "Period", "displayField": "label",
          "fields": [
            { "key": "label", "label": "Label", "type": "text" },
            { "key": "scenario", "label": "Scenario", "type": "reference", "targetEntity": "scenario" },
            { "key": "month", "label": "Month", "type": "date" },
            { "key": "month_end", "label": "Month end", "type": "date" },
            { "key": "seq", "label": "Order", "type": "integer" },
            { "key": "total", "label": "Total", "type": "money",
              "computed": { "rollup": { "entity": "hire", "via": "scenario", "match": "scenario",
                                        "op": "sum", "field": "cost", "window": {{window}} } } }
          ] }
      ]
    }
    """)!;

    private static List<string> Errors(string window) =>
        Gate.SemanticErrors(Doc(window)).Where(e => e.Contains("window")).ToList();

    [Fact]
    public void A_spanning_window_is_accepted() =>
        Assert.Empty(Errors("""{ "from": "starts", "to": "ends", "against": "month" }"""));

    /// <summary>An open-ended span needs only a start — that is what an open-ended hire is.</summary>
    [Fact]
    public void A_spanning_window_may_omit_its_end() =>
        Assert.Empty(Errors("""{ "from": "starts", "against": "month" }"""));

    [Fact]
    public void A_bucket_window_is_accepted() =>
        Assert.Empty(Errors("""{ "at": "starts", "within": { "from": "month", "to": "month_end" } }"""));

    /// <summary>Half-open buckets are legal: everything from this date on.</summary>
    [Fact]
    public void A_bucket_window_may_be_open_at_one_end() =>
        Assert.Empty(Errors("""{ "at": "starts", "within": { "from": "month" } }"""));

    /// <summary>The two directions are mutually exclusive. Naming both means the author had one of
    /// them in mind and the runtime would pick the other.</summary>
    [Fact]
    public void A_window_cannot_ask_both_questions() =>
        Assert.Contains(Errors("""
            { "from": "starts", "against": "month", "at": "starts",
              "within": { "from": "month", "to": "month_end" } }
            """), e => e.Contains("both 'against' and 'at'"));

    /// <summary>A window that names no direction at all filters nothing, and would silently report
    /// the whole plan's total in every single row.</summary>
    [Fact]
    public void A_window_with_no_direction_is_refused() =>
        Assert.Contains(Errors("""{ "from": "starts", "to": "ends" }"""),
            e => e.Contains("needs 'against'"));

    /// <summary>An unbounded bucket is every date. Refused rather than silently collecting the lot.</summary>
    [Fact]
    public void A_bucket_with_no_range_is_refused() =>
        Assert.Contains(Errors("""{ "at": "starts" }"""),
            e => e.Contains("needs a 'within' range"));

    [Fact]
    public void A_range_with_nothing_to_place_in_it_is_refused() =>
        Assert.Contains(Errors("""{ "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("no 'at' date"));

    /// <summary>An 'against' with no bound on the row leaves nothing to compare it to.</summary>
    [Fact]
    public void A_span_with_no_bounds_is_refused() =>
        Assert.Contains(Errors("""{ "against": "month" }"""),
            e => e.Contains("needs a 'from' and/or 'to'"));

    // ---- the fields themselves --------------------------------------------------------------------

    /// <summary>'at' belongs to the AGGREGATED entity, and naming one of this record's own fields is
    /// the mistake the inverted direction invites.</summary>
    [Fact]
    public void A_bucket_date_must_be_a_field_of_the_aggregated_entity() =>
        Assert.Contains(Errors("""{ "at": "month", "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("'at' field 'month' is not a field of 'hire'"));

    /// <summary>…and 'within' belongs to THIS one, which is the same mistake in reverse.</summary>
    [Fact]
    public void A_bucket_range_must_be_fields_of_this_entity() =>
        Assert.Contains(Errors("""{ "at": "starts", "within": { "from": "starts", "to": "ends" } }"""),
            e => e.Contains("'within.from' field 'starts' is not a field of 'period'"));

    /// <summary>A window orders dates or numbers. A mistyped field here aggregates nothing rather
    /// than failing, which is the worst outcome a plan can have.</summary>
    [Fact]
    public void A_window_field_that_orders_nothing_is_refused() =>
        Assert.Contains(Errors("""{ "at": "role", "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("is a text; a window orders dates or numbers"));

    /// <summary>
    /// Bounds may be NUMBERS — "the first six periods" rather than a stretch of the calendar.
    ///
    /// <para>A growth rate is decided as a phase of the plan, and expressing that in dates means
    /// recomputing every boundary whenever the plan start moves. Same question, different scale.</para>
    /// </summary>
    [Fact]
    public void A_window_may_be_bounded_in_numbers() =>
        Assert.Empty(Errors("""{ "from": "cost", "to": "cost", "against": "seq" }"""));

    /// <summary>But not both. A sequence compared against a date is not a narrower window, it is an
    /// empty one — and an empty window aggregates in silence.</summary>
    [Fact]
    public void A_window_cannot_mix_dates_and_numbers() =>
        Assert.Contains(Errors("""{ "from": "starts", "to": "ends", "against": "seq" }"""),
            e => e.Contains("mixes dates and numbers"));
}
