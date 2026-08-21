// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>The hand-authored room-booking reference app (COMPOSITION_REFRAME_FINAL.md Phase 1).
/// It is the proof that today's runtime can express a purpose-built scheduling surface — a
/// rooms × next-7-days availability grid with editable create-on-first-write cells, an approval
/// flow, and a subject hub — WITHOUT the screen DSL. Pinning it clean here makes it the regression
/// anchor every later phase's runtime additions must keep passing.</summary>
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
        // The signature surface: a page-level nested-repeat grid (rooms as rows, a 7-day date axis as
        // columns, morning/afternoon/evening slots as a THIRD axis) whose innermost node is a 3-key
        // editable `cell` on the booking join that also opens a detail popup. This is the enriched
        // "global" cardinality grid the plan's availabilityPlanner pattern will compile to.
        var doc = (JsonObject)Reference();
        var availability = doc["pages"]!.AsArray()
            .First(p => p!["key"]!.GetValue<string>() == "availability")!.AsObject();

        // Both the week grid (date = each day column) and the day board (date = the selected cursor)
        // end in an editable, openable 3-key slot cell — so booking and the popup work in either view.
        var cells = new List<JsonObject>();
        Collect(availability["blocks"], "cell", cells);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.Equal("booking", cell["entity"]!.GetValue<string>());
            Assert.True(cell["editable"]!.GetValue<bool>());       // click an empty slot to book
            Assert.True(cell["openDetail"]!.GetValue<bool>());     // click a filled slot to open the popup
            var keys = cell["keys"]!.AsObject();
            Assert.Equal("{{room.id}}", keys["room"]!.GetValue<string>());
            Assert.Equal("{{slot.value}}", keys["slot"]!.GetValue<string>());
        });
        var dateKeys = cells.Select(c => c["keys"]!["date"]!.GetValue<string>()).ToList();
        Assert.Contains("{{day.date}}", dateKeys);        // week grid: the day column
        Assert.Contains("{{state.cursor}}", dateKeys);    // day board: the selected day
    }

    [Fact]
    public void Availability_page_declares_screen_state_and_a_toggle_control()
    {
        // Flexible controls: the page holds local state (view mode + week cursor) and a segmented
        // control toggles it — the day/week switch that proves state + UI events work end to end.
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
        // Tier-gated approval: a booking's status starts 'approved' when its ROOM's tier is 'standard'
        // (a one-hop cross-record condition), otherwise 'pending' — so standard rooms confirm instantly
        // and premium rooms need approval. This is the primitive the composition plan called out.
        //
        // It used to live in `field.initial`, which put the process's entry rule on the field instead
        // of on the process — the exact "hidden business logic on a field" the schema review flagged.
        // It now lives where the states live; the compiler lowers it back onto `initial`, so the
        // RUNTIME behaviour is unchanged (see CompilerTests.Conditional_process_entry_*).
        var doc = (JsonObject)Reference();
        var booking = doc["entities"]!.AsArray().First(e => e!["key"]!.GetValue<string>() == "booking")!.AsObject();
        var status = booking["fields"]!.AsArray().First(f => f!["key"]!.GetValue<string>() == "status")!.AsObject();
        Assert.Null(status["initial"]);
        Assert.Null(status["default"]);
        Assert.Null(status["options"]);   // the process's states are its options

        var entry = doc["processes"]!.AsArray()
            .First(p => p!["entity"]!.GetValue<string>() == "booking")!["initialState"]!.AsObject();
        Assert.Equal("pending", entry["fallback"]!.GetValue<string>());
        var rule = entry["rules"]!.AsArray().Single()!.AsObject();
        Assert.Equal("room.tier", rule["when"]!["path"]!.GetValue<string>());   // hops the room reference
        Assert.Equal("approved", rule["state"]!.GetValue<string>());
    }

    [Fact]
    public void Reference_models_attendees_as_a_subordinate_child()
    {
        // "Appointments need details — attendees etc." A subordinate booking_attendee entity so the
        // booking detail (and its popup) can show who's coming; ownedBy keeps it out of top-level nav.
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
