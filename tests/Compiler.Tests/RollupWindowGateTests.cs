// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class RollupWindowGateTests
{
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

    [Fact]
    public void A_spanning_window_may_omit_its_end() =>
        Assert.Empty(Errors("""{ "from": "starts", "against": "month" }"""));

    [Fact]
    public void A_bucket_window_is_accepted() =>
        Assert.Empty(Errors("""{ "at": "starts", "within": { "from": "month", "to": "month_end" } }"""));

    [Fact]
    public void A_bucket_window_may_be_open_at_one_end() =>
        Assert.Empty(Errors("""{ "at": "starts", "within": { "from": "month" } }"""));

    [Fact]
    public void A_window_cannot_ask_both_questions() =>
        Assert.Contains(Errors("""
            { "from": "starts", "against": "month", "at": "starts",
              "within": { "from": "month", "to": "month_end" } }
            """), e => e.Contains("both 'against' and 'at'"));

    [Fact]
    public void A_window_with_no_direction_is_refused() =>
        Assert.Contains(Errors("""{ "from": "starts", "to": "ends" }"""),
            e => e.Contains("needs 'against'"));

    [Fact]
    public void A_bucket_with_no_range_is_refused() =>
        Assert.Contains(Errors("""{ "at": "starts" }"""),
            e => e.Contains("needs a 'within' range"));

    [Fact]
    public void A_range_with_nothing_to_place_in_it_is_refused() =>
        Assert.Contains(Errors("""{ "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("no 'at' date"));

    [Fact]
    public void A_span_with_no_bounds_is_refused() =>
        Assert.Contains(Errors("""{ "against": "month" }"""),
            e => e.Contains("needs a 'from' and/or 'to'"));

    [Fact]
    public void A_bucket_date_must_be_a_field_of_the_aggregated_entity() =>
        Assert.Contains(Errors("""{ "at": "month", "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("'at' field 'month' is not a field of 'hire'"));

    [Fact]
    public void A_bucket_range_must_be_fields_of_this_entity() =>
        Assert.Contains(Errors("""{ "at": "starts", "within": { "from": "starts", "to": "ends" } }"""),
            e => e.Contains("'within.from' field 'starts' is not a field of 'period'"));

    [Fact]
    public void A_window_field_that_orders_nothing_is_refused() =>
        Assert.Contains(Errors("""{ "at": "role", "within": { "from": "month", "to": "month_end" } }"""),
            e => e.Contains("is a text; a window orders dates or numbers"));

    [Fact]
    public void A_window_may_be_bounded_in_numbers() =>
        Assert.Empty(Errors("""{ "from": "cost", "to": "cost", "against": "seq" }"""));

    [Fact]
    public void A_window_cannot_mix_dates_and_numbers() =>
        Assert.Contains(Errors("""{ "from": "starts", "to": "ends", "against": "seq" }"""),
            e => e.Contains("mixes dates and numbers"));
}
