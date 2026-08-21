// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The day view's two invariants, both about a surface that would LOOK broken rather than
/// misconfigured if they were only conventions.
///
/// <para>A day view rules an hour axis. Anchored on a plain <c>date</c> there is no time of day to
/// place anything at, so every record piles onto hour zero. And an axis whose end is not after its
/// start has no height to draw into, so it renders as an empty box. Neither reads as "the author
/// wrote something wrong", which is exactly why the gate has to say so instead.</para>
/// </summary>
public class DayViewGateTests
{
    private static JsonObject Definition(JsonObject calendarBlock, string startType = "datetime")
    {
        return (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion": "2.0", "key": "sched", "name": "Schedule", "version": "1.0.0",
          "entities": [
            { "key": "meeting", "label": "Meeting", "labelPlural": "Meetings", "displayField": "title",
              "fields": [
                { "key": "title", "label": "Title", "type": "text", "required": true },
                { "key": "starts_at", "label": "Starts", "type": "{{startType}}" },
                { "key": "ends_at", "label": "Ends", "type": "{{startType}}" },
                { "key": "note", "label": "Note", "type": "text" }
              ]}
          ],
          "pages": [
            { "key": "day", "label": "Day", "entity": "meeting", "blocks": [ {{calendarBlock.ToJsonString()}} ] }
          ],
          "roles": [
            { "key": "admin", "name": "Admin",
              "grants": [ { "entity": "meeting", "create": true, "read": true, "update": true, "delete": true } ] }
          ]
        }
        """)!;
    }

    private static JsonObject Calendar(params (string Key, JsonNode? Value)[] extra)
    {
        var b = new JsonObject
        {
            ["kind"] = "calendar",
            ["startField"] = "starts_at",
            ["source"] = new JsonObject { ["entity"] = "meeting" },
        };
        foreach (var (k, v) in extra) b[k] = v;
        return b;
    }

    private static List<string> Errors(JsonObject block, string startType = "datetime") =>
        Gate.Validate(Definition(block, startType)).ToList();

    [Fact]
    public void A_day_range_over_a_datetime_is_fine()
    {
        var errors = Errors(Calendar(("range", "day"), ("endField", "ends_at")));
        Assert.DoesNotContain(errors, e => e.Contains("day") || e.Contains("endField"));
    }

    [Fact]
    public void A_day_range_over_a_plain_date_is_refused_and_says_why()
    {
        var errors = Errors(Calendar(("range", "day")), startType: "date");
        Assert.Contains(errors, e => e.Contains("range 'day'") && e.Contains("time of day"));
    }

    [Fact]
    public void The_other_ranges_are_still_fine_over_a_plain_date()
    {
        // Only the HOUR axis needs a time. A month grid over a due date is the original use.
        foreach (var range in new[] { "week", "month", "year" })
        {
            var errors = Errors(Calendar(("range", range)), startType: "date");
            Assert.DoesNotContain(errors, e => e.Contains("range"));
        }
    }

    [Fact]
    public void An_end_field_that_is_not_a_date_is_refused()
    {
        var errors = Errors(Calendar(("endField", "note")));
        Assert.Contains(errors, e => e.Contains("endField") && e.Contains("note"));
    }

    [Fact]
    public void An_end_field_that_does_not_exist_is_refused()
    {
        var errors = Errors(Calendar(("endField", "nope")));
        Assert.Contains(errors, e => e.Contains("endField") && e.Contains("nope"));
    }

    [Fact]
    public void An_axis_that_ends_before_it_starts_is_refused()
    {
        var axis = new JsonObject { ["startHour"] = 18, ["endHour"] = 9 };
        var errors = Errors(Calendar(("range", "day"), ("timeAxis", axis)));
        Assert.Contains(errors, e => e.Contains("timeAxis") && e.Contains("endHour"));
    }

    [Fact]
    public void A_sane_axis_passes()
    {
        var axis = new JsonObject { ["startHour"] = 8, ["endHour"] = 18, ["slotMinutes"] = 30 };
        var errors = Errors(Calendar(("range", "day"), ("timeAxis", axis)));
        Assert.DoesNotContain(errors, e => e.Contains("timeAxis"));
    }
}
