// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class HeaderAndTrendGateTests
{
    private static JsonObject Definition(string state, string blocks, string detail = "[]", string subtitle = "Desk")
    {
        var detailJson = detail == "[]" ? "" : $$""", "detail": { "blocks": {{detail}} }""";
        return (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion": "2.0", "key": "desk", "name": "Desk", "version": "1.0.0",
          "entities": [
            { "key": "ticket", "label": "Ticket", "labelPlural": "Tickets", "displayField": "subject",
              "fields": [
                { "key": "subject", "label": "Subject", "type": "text" },
                { "key": "opened_on", "label": "Opened", "type": "date" },
                { "key": "hours", "label": "Hours", "type": "decimal" }
              ]{{detailJson}} }
          ],
          "pages": [
            { "key": "overview", "label": "Overview", "entity": "ticket", "subtitle": "{{subtitle}}",
              "state": {{state}}, "blocks": {{blocks}} }
          ],
          "roles": [
            { "key": "admin", "name": "Admin",
              "grants": [ { "entity": "ticket", "create": true, "read": true, "update": true, "delete": true } ] }
          ]
        }
        """)!;
    }

    private const string Month = """[ { "key": "period", "type": "period", "default": "thisMonth" } ]""";
    private const string HeaderPeriod = """{ "kind": "period", "stateKey": "period", "presentation": "menu", "units": ["month"], "placement": "header" }""";

    private static string Tiles(string trend, string filters = """
        [ { "field": "opened_on", "operator": "gte", "value": "{{state.period.from}}" },
          { "field": "opened_on", "operator": "lt", "value": "{{state.period.next}}" } ]
        """, string extra = "") => $$"""
        { "kind": "tiles", "tiles": [
          { "label": "Received", {{extra}} "trend": {{trend}},
            "source": { "entity": "ticket", "aggregate": { "op": "count" }, "filters": {{filters}} } } ] }
        """;

    private static List<string> Errors(string state, string blocks, string detail = "[]", string subtitle = "Desk") =>
        Gate.Validate(Definition(state, blocks, detail, subtitle)).ToList();

    [Fact]
    public void A_header_period_with_a_menu_and_a_trend_tile_over_its_window_is_fine()
    {
        var blocks = $$"""[ {{HeaderPeriod}}, {{Tiles("""{ "good": "below" }""")}} ]""";
        Assert.Empty(Errors(Month, blocks, subtitle: "In {{state.period.label}}"));
    }

    [Fact]
    public void Only_a_period_or_a_control_can_move_to_the_title_row()
    {
        var blocks = """[ { "kind": "text", "value": "Hi", "placement": "header" } ]""";
        Assert.Contains(Errors("[]", blocks), e => e.Contains("/placement"));
        Assert.Contains(Gate.SemanticErrors(Definition("[]", blocks)), e => e.Contains("'text' cannot have placement 'header'"));
    }

    [Fact]
    public void A_header_block_must_sit_directly_in_the_page()
    {
        var blocks = $$"""[ { "kind": "section", "label": "Top", "blocks": [ {{HeaderPeriod}} ] } ]""";
        Assert.Contains(Errors(Month, blocks), e => e.Contains("sits inside another block"));
    }

    [Fact]
    public void A_record_detail_has_no_title_row()
    {
        var detail = """[ { "kind": "control", "control": "segmented", "stateKey": "d", "placement": "header" } ]""";
        Assert.Contains(Gate.SemanticErrors(Definition("[]", """[ { "kind": "text", "value": "x" } ]""", detail)),
            e => e.Contains("which only a page has"));
    }

    [Fact]
    public void A_trend_needs_a_period_in_its_source()
    {
        var blocks = $$"""[ {{Tiles("""{ "good": "above" }""", "[]")}} ]""";
        Assert.Contains(Errors("[]", blocks), e => e.Contains("must read a 'period' state of the page"));
    }

    [Fact]
    public void A_trend_reads_exactly_one_period()
    {
        var two = """
            [ { "key": "period", "type": "period", "default": "thisMonth" },
              { "key": "other", "type": "period", "default": "thisWeek" } ]
            """;
        var filters = """
            [ { "field": "opened_on", "operator": "gte", "value": "{{state.period.from}}" },
              { "field": "opened_on", "operator": "lt", "value": "{{state.other.next}}" } ]
            """;
        Assert.Contains(Errors(two, $$"""[ {{Tiles("""{ "good": "above" }""", filters)}} ]"""),
            e => e.Contains("reads 2 periods"));
    }

    [Fact]
    public void A_trend_does_its_own_comparison()
    {
        var filters = """
            [ { "field": "opened_on", "operator": "gte", "value": "{{state.period.prev.from}}" },
              { "field": "opened_on", "operator": "lt", "value": "{{state.period.prev.next}}" } ]
            """;
        Assert.Contains(Errors(Month, $$"""[ {{Tiles("""{ "good": "above" }""", filters)}} ]"""),
            e => e.Contains("a trend makes the comparison itself"));
    }

    [Fact]
    public void A_trend_source_cannot_read_today()
    {
        var filters = """
            [ { "field": "opened_on", "operator": "gte", "value": "{{state.period.from}}" },
              { "field": "opened_on", "operator": "lt", "value": "{{today}}" } ]
            """;
        Assert.Contains(Errors(Month, $$"""[ {{Tiles("""{ "good": "above" }""", filters)}} ]"""),
            e => e.Contains("{{today}} or {{now}}"));
    }

    [Fact]
    public void A_future_window_has_no_trend()
    {
        var ahead = """[ { "key": "period", "type": "period", "default": "next30Days" } ]""";
        Assert.Contains(Errors(ahead, $$"""[ {{Tiles("""{ "good": "above" }""")}} ]"""),
            e => e.Contains("runs into the future"));
    }

    [Fact]
    public void A_share_has_no_trend_yet()
    {
        var blocks = $$"""[ {{Tiles("""{ "good": "above" }""", extra: "\"format\": \"share\", \"max\": 10,")}} ]""";
        Assert.Contains(Errors(Month, blocks), e => e.Contains("a trend on it is not offered yet"));
    }

    [Fact]
    public void A_trend_needs_to_know_which_way_is_good()
    {
        Assert.NotEmpty(Errors(Month, $$"""[ {{Tiles("""{ "show": "delta" }""")}} ]"""));
    }

    [Fact]
    public void A_record_tile_has_no_trend()
    {
        var detail = """[ { "kind": "tiles", "tiles": [ { "label": "Hours", "field": "hours", "trend": { "good": "above" } } ] } ]""";
        Assert.Contains(Errors("[]", """[ { "kind": "text", "value": "x" } ]""", detail),
            e => e.Contains("a record's field has no previous period"));
    }

    [Fact]
    public void A_page_subtitle_is_held_to_the_period_members()
    {
        Assert.Contains(Errors(Month, $"[ {HeaderPeriod} ]", subtitle: "In {{state.period.month}}"),
            e => e.Contains("has no member 'month'"));
    }

    [Fact]
    public void A_section_takes_a_subtitle_and_a_period_takes_a_presentation()
    {
        var blocks = $$"""
            [ {{HeaderPeriod}},
              { "kind": "section", "label": "Queue", "subtitle": "All open tickets, soonest due first.",
                "blocks": [ { "kind": "text", "value": "x" } ] } ]
            """;
        Assert.Empty(Errors(Month, blocks));
        var bad = """[ { "kind": "period", "stateKey": "period", "presentation": "wheel" } ]""";
        Assert.NotEmpty(Errors(Month, bad));
    }
}
