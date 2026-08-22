// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class CombinedSourceGateTests
{
    private static JsonObject WithBlock(string blockJson) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "holding", "label": "Holding", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "sector", "label": "Sector", "type": "select",
              "options": [ { "value": "a", "label": "A" }, { "value": "b", "label": "B" } ] },
            { "key": "cost", "label": "Cost", "type": "money" },
            { "key": "value", "label": "Value", "type": "money" }
          ] }
      ],
      "pages": [
        { "key": "overview", "label": "Overview", "entity": "holding", "blocks": [ {{blockJson}} ] }
      ]
    }
    """)!;

    private static List<string> Errors(string blockJson) => Gate.Validate(WithBlock(blockJson));

    private const string SumValue = """{ "entity": "holding", "aggregate": { "op": "sum", "field": "value" } }""";
    private const string SumCost = """{ "entity": "holding", "aggregate": { "op": "sum", "field": "cost" } }""";

    [Fact]
    public void A_ratio_of_two_sums_is_valid() =>
        Assert.Empty(Errors($$"""
        { "kind": "stat", "label": "Multiple", "format": "multiple",
          "combine": { "mode": "ratio" }, "sources": [ {{SumValue}}, {{SumCost}} ] }
        """));

    [Fact]
    public void Sources_without_a_combine_mode_is_rejected() =>
        Assert.Contains(Errors($$"""
        { "kind": "stat", "label": "Multiple", "sources": [ {{SumValue}}, {{SumCost}} ] }
        """), e => e.Contains("needs a 'combine' mode"));

    [Fact]
    public void Combine_without_sources_is_rejected() =>
        Assert.Contains(Errors($$"""
        { "kind": "stat", "label": "Multiple", "combine": { "mode": "ratio" }, "source": {{SumValue}} }
        """), e => e.Contains("'combine' needs 'sources'"));

    [Fact]
    public void A_ratio_of_three_sources_is_rejected() =>
        Assert.Contains(Errors($$"""
        { "kind": "stat", "label": "Multiple", "combine": { "mode": "ratio" },
          "sources": [ {{SumValue}}, {{SumCost}}, {{SumCost}} ] }
        """), e => e.Contains("'ratio' takes exactly 2 sources"));

    [Fact]
    public void Source_and_sources_together_are_rejected() =>
        Assert.Contains(Errors($$"""
        { "kind": "stat", "label": "Multiple", "combine": { "mode": "sum" },
          "source": {{SumValue}}, "sources": [ {{SumValue}}, {{SumCost}} ] }
        """), e => e.Contains("both 'source' and 'sources'"));

    [Fact]
    public void A_combined_source_still_has_its_aggregate_field_checked() =>
        Assert.Contains(Errors("""
        { "kind": "stat", "label": "Multiple", "combine": { "mode": "ratio" },
          "sources": [ { "entity": "holding", "aggregate": { "op": "sum", "field": "ghost" } },
                       { "entity": "holding", "aggregate": { "op": "sum", "field": "cost" } } ] }
        """), e => e.Contains("source 1") && e.Contains("'ghost' is not a field"));

    [Fact]
    public void A_share_of_a_collection_aggregate_is_valid_inside_a_repeat() =>
        Assert.Empty(Errors($$"""
        { "kind": "repeat", "as": "row", "source": { "entity": "holding" },
          "blocks": [ { "kind": "stat", "label": "Share", "field": "value", "format": "share",
                        "max": { "source": {{SumValue}} } } ] }
        """));

    [Fact]
    public void A_share_without_a_denominator_is_rejected() =>
        Assert.Contains(Errors("""
        { "kind": "repeat", "as": "row", "source": { "entity": "holding" },
          "blocks": [ { "kind": "stat", "label": "Share", "field": "value", "format": "share" } ] }
        """), e => e.Contains("'share' needs a 'max'"));

    [Fact]
    public void A_stat_max_naming_an_unknown_field_is_rejected() =>
        Assert.Contains(Errors("""
        { "kind": "repeat", "as": "row", "source": { "entity": "holding" },
          "blocks": [ { "kind": "stat", "label": "Share", "field": "value", "max": "ghost" } ] }
        """), e => e.Contains("stat max 'ghost' is not a field"));

    [Fact]
    public void Two_series_sharing_one_axis_are_valid() =>
        Assert.Empty(Errors("""
        { "kind": "chart", "chartType": "bar", "label": "Cost vs value",
          "sources": [
            { "label": "Cost",  "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "cost",  "groupBy": "sector" } } },
            { "label": "Value", "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "value", "groupBy": "sector" } } } ] }
        """));

    [Fact]
    public void Series_that_group_by_different_things_are_rejected() =>
        Assert.Contains(Errors("""
        { "kind": "chart", "chartType": "bar",
          "sources": [
            { "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "cost",  "groupBy": "sector" } } },
            { "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "value", "groupBy": "name" } } } ] }
        """), e => e.Contains("every series must share one axis"));

    [Fact]
    public void A_donut_cannot_hold_more_than_one_series() =>
        Assert.Contains(Errors("""
        { "kind": "chart", "chartType": "donut",
          "sources": [
            { "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "cost",  "groupBy": "sector" } } },
            { "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "value", "groupBy": "sector" } } } ] }
        """), e => e.Contains("draws one series"));

    private static List<string> FieldErrors(string fieldJson) => Gate.Validate((JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "thing", "label": "Thing", "displayField": "name",
          "fields": [ { "key": "name", "label": "Name", "type": "text" }, {{fieldJson}} ] }
      ]
    }
    """)!);

    [Theory]
    [InlineData("""{ "key": "rate", "label": "Rate", "type": "decimal", "scale": 1, "unit": "%" }""")]
    [InlineData("""{ "key": "steps", "label": "Steps", "type": "integer", "unit": " steps" }""")]
    public void A_unit_on_a_bare_number_is_valid(string fieldJson) => Assert.Empty(FieldErrors(fieldJson));

    [Fact]
    public void A_unit_on_money_is_rejected_because_currency_already_says_it() =>
        Assert.Contains(
            FieldErrors("""{ "key": "fee", "label": "Fee", "type": "money", "currency": "EUR", "unit": "x" }"""),
            e => e.Contains("has a 'unit' but is a 'money'"));

    [Fact]
    public void A_unit_on_a_date_is_rejected() =>
        Assert.Contains(
            FieldErrors("""{ "key": "due", "label": "Due", "type": "date", "unit": "%" }"""),
            e => e.Contains("has a 'unit' but is a 'date'"));

    [Fact]
    public void A_single_source_chart_is_unchanged() =>
        Assert.Empty(Errors("""
        { "kind": "chart", "chartType": "bar",
          "source": { "entity": "holding", "aggregate": { "op": "sum", "field": "value", "groupBy": "sector" } } }
        """));
}
