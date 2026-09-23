// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class PeriodGateTests
{
    private static JsonObject Definition(string state, string blocks, string detail = "[]")
    {
        if (blocks == "[]") blocks = """[ { "kind": "text", "value": "Week" } ]""";
        var detailJson = detail == "[]" ? "" : $$""", "detail": { "blocks": {{detail}} }""";
        return (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion": "2.0", "key": "hours", "name": "Hours", "version": "1.0.0",
          "entities": [
            { "key": "entry", "label": "Entry", "labelPlural": "Entries", "displayField": "note",
              "fields": [
                { "key": "note", "label": "Note", "type": "text" },
                { "key": "day", "label": "Day", "type": "date" },
                { "key": "logged_at", "label": "Logged at", "type": "datetime" },
                { "key": "hours", "label": "Hours", "type": "decimal" }
              ]{{detailJson}} }
          ],
          "pages": [
            { "key": "week", "label": "Week", "entity": "entry", "state": {{state}}, "blocks": {{blocks}} }
          ],
          "roles": [
            { "key": "admin", "name": "Admin",
              "grants": [ { "entity": "entry", "create": true, "read": true, "update": true, "delete": true } ] }
          ]
        }
        """)!;
    }

    private const string Week = """[ { "key": "p", "type": "period", "default": "thisWeek" } ]""";

    private static string Stat(string op, string value, string field = "day") => $$"""
        { "kind": "stat", "label": "Hours",
          "source": { "entity": "entry", "aggregate": { "op": "sum", "field": "hours" },
            "filters": [ { "field": "{{field}}", "operator": "{{op}}", "value": "{{value}}" } ] } }
        """;

    private const string Control = """{ "kind": "period", "stateKey": "p" }""";

    private static List<string> Errors(string state, string blocks, string detail = "[]") =>
        Gate.Validate(Definition(state, blocks, detail)).ToList();

    [Fact]
    public void A_period_state_opening_on_a_preset_with_its_control_and_half_open_bounds_is_fine()
    {
        var blocks = $"[ {Control}, {Stat("gte", "{{state.p.from}}")}, {Stat("lt", "{{state.p.next}}")} ]";
        Assert.Empty(Errors(Week, blocks));
    }

    [Fact]
    public void A_period_state_needs_a_known_preset_as_its_default()
    {
        Assert.Contains(Errors("""[ { "key": "p", "type": "period" } ]""", "[]"),
            e => e.Contains("period state 'p' needs a preset"));
        Assert.Contains(Errors("""[ { "key": "p", "type": "period", "default": "fortnight" } ]""", "[]"),
            e => e.Contains("period state 'p' needs a preset") && e.Contains("thisWeek"));
    }

    [Fact]
    public void A_period_control_must_name_a_period_state_of_its_page()
    {
        Assert.Contains(Errors("[]", $"[ {Control} ]"), e => e.Contains("stateKey 'p' is not a 'period' state"));
        Assert.Contains(Errors("""[ { "key": "p", "type": "date", "default": "{{today}}" } ]""", $"[ {Control} ]"),
            e => e.Contains("stateKey 'p' is not a 'period' state"));
    }

    [Fact]
    public void A_period_control_must_offer_the_unit_its_state_opens_on()
    {
        var monthOnly = """[ { "kind": "period", "stateKey": "p", "units": ["month"] } ]""";
        Assert.Contains(Errors(Week, monthOnly), e => e.Contains("add 'week' to its units"));
        var both = """[ { "kind": "period", "stateKey": "p", "units": ["week", "month"] } ]""";
        Assert.Empty(Errors(Week, both));
    }

    [Fact]
    public void A_period_control_on_a_record_detail_is_refused()
    {
        Assert.Contains(Errors("[]", "[]", $"[ {Control} ]"), e => e.Contains("belongs directly on a page"));
    }

    [Fact]
    public void A_bare_period_token_and_an_unknown_member_are_refused()
    {
        Assert.Contains(Errors(Week, $"[ {Stat("gte", "{{state.p}}")} ]"), e => e.Contains("is a whole period"));
        Assert.Contains(Errors(Week, $"[ {Stat("gte", "{{state.p.start}}")} ]"), e => e.Contains("has no member 'start'"));
        Assert.Empty(Errors(Week, """[ { "kind": "text", "value": "Hours for {{state.p.label}}, against {{state.p.prev.label}}" } ]"""));
    }

    [Fact]
    public void Lt_the_last_day_is_refused_because_it_drops_that_day()
    {
        Assert.Contains(Errors(Week, $"[ {Stat("lt", "{{state.p.to}}")} ]"), e => e.Contains("use 'lt {{state.p.next}}'"));
        Assert.Contains(Errors(Week, $"[ {Stat("lt", "{{state.p.prev.to}}")} ]"), e => e.Contains("use 'lt {{state.p.prev.next}}'"));
    }

    [Fact]
    public void Lte_the_last_day_is_refused_on_a_timestamp_and_fine_on_a_date()
    {
        Assert.Contains(Errors(Week, $"[ {Stat("lte", "{{state.p.to}}", "logged_at")} ]"),
            e => e.Contains("timestamp 'logged_at'"));
        Assert.Empty(Errors(Week, $"[ {Stat("lte", "{{state.p.to}}", "day")} ]"));
    }

    [Fact]
    public void A_dates_axis_must_end_on_the_inclusive_last_day()
    {
        string Axis(string to) => $$$"""
            [ { "kind": "repeat", "as": "d", "direction": "row",
                "source": { "dates": { "from": "{{state.p.from}}", "to": "{{{to}}}", "step": "day" } },
                "blocks": [ { "kind": "text", "value": "{{d.label}}" } ] } ]
            """;
        Assert.Contains(Errors(Week, Axis("{{state.p.next}}")), e => e.Contains("one column too many"));
        Assert.Empty(Errors(Week, Axis("{{state.p.to}}")));
    }

    [Fact]
    public void The_period_is_taught_as_control_period()
    {
        Assert.DoesNotContain("period", BlockKinds.NotAuthorable);
        Assert.Equal(["control.period"], BlockKinds.ByCanonical["period"]);
    }

    [Fact]
    public void The_model_is_taught_the_period_state_type_with_its_control_on_pages_only()
    {
        var taught = Schemas.AuthoringSchema()["$defs"]!["screenState"]!["items"]!["properties"]!["type"]!["enum"]!.AsArray();
        Assert.Contains(taught, t => t!.GetValue<string>() == "period");
        var said = Schemas.AuthoringSchema()["$defs"]!["screenState"]!["items"]!["properties"]!["default"]!["description"]!.GetValue<string>();
        Assert.Contains("thisWeek", said);
        Assert.True(Schemas.AuthoringSchema("page")["$defs"]!.AsObject().ContainsKey("block_control_period"));
        Assert.False(Schemas.AuthoringSchema("detail")["$defs"]!.AsObject().ContainsKey("block_control_period"));
    }
}
