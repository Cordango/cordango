// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class ReferenceAppTests
{
    private static JsonNode Reference() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "room-booking.appdef.json")))!;

    [Fact]
    public void Room_booking_reference_passes_the_gate_un_salvaged()
    {
        var errors = Gate.Validate(Reference());
        Assert.Empty(errors);
    }

    [Fact]
    public void Reference_composes_the_availability_grid_from_primitives()
    {
        var doc = (JsonObject)Reference();
        var availability = doc["pages"]!.AsArray()
            .First(p => p!["key"]!.GetValue<string>() == "availability")!.AsObject();

        var cells = new List<JsonObject>();
        Collect(availability["blocks"], "cell", cells);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.Equal("booking", cell["entity"]!.GetValue<string>());
            Assert.True(cell["editable"]!.GetValue<bool>());
            Assert.True(cell["openDetail"]!.GetValue<bool>());
            var keys = cell["keys"]!.AsObject();
            Assert.Equal("{{room.id}}", keys["room"]!.GetValue<string>());
            Assert.Equal("{{slot.value}}", keys["slot"]!.GetValue<string>());
        });
        var dateKeys = cells.Select(c => c["keys"]!["date"]!.GetValue<string>()).ToList();
        Assert.Contains("{{day.date}}", dateKeys);
        Assert.Contains("{{state.cursor}}", dateKeys);
    }

    [Fact]
    public void Availability_page_declares_screen_state_and_a_toggle_control()
    {
        var doc = (JsonObject)Reference();
        var availability = doc["pages"]!.AsArray()
            .First(p => p!["key"]!.GetValue<string>() == "availability")!.AsObject();
        var stateKeys = availability["state"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()).ToList();
        Assert.Contains("mode", stateKeys);
        Assert.Contains("cursor", stateKeys);

        var controls = new List<JsonObject>();
        Collect(availability["blocks"], "control", controls);
        Assert.Contains(controls, c => c["control"]!.GetValue<string>() == "segmented" && c["stateKey"]!.GetValue<string>() == "mode");
        Assert.Contains(controls, c => c["control"]!.GetValue<string>() == "stepper");
    }

    [Fact]
    public void Booking_status_is_tier_gated_via_conditional_process_entry()
    {
        var doc = (JsonObject)Reference();
        var booking = doc["entities"]!.AsArray().First(e => e!["key"]!.GetValue<string>() == "booking")!.AsObject();
        var status = booking["fields"]!.AsArray().First(f => f!["key"]!.GetValue<string>() == "status")!.AsObject();
        Assert.Null(status["initial"]);
        Assert.Null(status["default"]);
        Assert.Null(status["options"]);

        var entry = doc["processes"]!.AsArray()
            .First(p => p!["entity"]!.GetValue<string>() == "booking")!["initialState"]!.AsObject();
        Assert.Equal("pending", entry["fallback"]!.GetValue<string>());
        var rule = entry["rules"]!.AsArray().Single()!.AsObject();
        Assert.Equal("room.tier", rule["when"]!["path"]!.GetValue<string>());
        Assert.Equal("approved", rule["state"]!.GetValue<string>());
    }

    [Fact]
    public void Reference_models_attendees_as_a_subordinate_child()
    {
        var doc = (JsonObject)Reference();
        var attendee = doc["entities"]!.AsArray()
            .FirstOrDefault(e => e!["key"]!.GetValue<string>() == "booking_attendee")?.AsObject();
        Assert.NotNull(attendee);
        Assert.Equal("booking", attendee!["ownedBy"]!["parent"]!.GetValue<string>());
    }

    private static void Collect(JsonNode? blocks, string kind, List<JsonObject> hits)
    {
        if (blocks is not JsonArray arr) return;
        foreach (var bn in arr)
        {
            if (bn is not JsonObject b) continue;
            if (b["kind"]?.GetValue<string>() == kind) hits.Add(b);
            Collect(b["blocks"], kind, hits);
            if (b["columns"] is JsonArray cols) foreach (var c in cols) Collect(c, kind, hits);
        }
    }
}
