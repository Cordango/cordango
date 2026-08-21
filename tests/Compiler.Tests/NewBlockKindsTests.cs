// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>The four composable surfaces added alongside the namespaced families:
/// data.table, data.calendar, domain.form and layout.split. Each is a real canonical block kind,
/// so each gets the same treatment as the rest: structural shape, semantic resolution of every key
/// it names, and a lowering path from its authoring name.</summary>
public class NewBlockKindsTests
{
    /// <summary>A 2-entity app: `booking` (dates + a select + a ref to `room`) and `room`.</summary>
    private static JsonObject App(params JsonNode[] pageBlocks)
    {
        var doc = (JsonObject)JsonNode.Parse("""
        {
          "schemaVersion": "2.0", "key": "rb", "name": "RB", "version": "1.0.0",
          "entities": [
            { "key": "room", "label": "Room", "displayField": "name", "fields": [
              { "key": "name", "label": "Name", "type": "text" } ] },
            { "key": "booking", "label": "Booking", "displayField": "purpose", "fields": [
              { "key": "purpose", "label": "Purpose", "type": "text" },
              { "key": "room", "label": "Room", "type": "reference", "targetEntity": "room" },
              { "key": "day", "label": "Day", "type": "date" },
              { "key": "slot", "label": "Slot", "type": "select",
                "options": [{ "value": "am", "label": "AM" }, { "value": "pm", "label": "PM" }] } ] }
          ]
        }
        """)!;
        var blocks = new JsonArray();
        foreach (var b in pageBlocks) blocks.Add(b);
        doc["pages"] = new JsonArray(new JsonObject
        {
            ["key"] = "home", ["label"] = "Home", ["blocks"] = blocks,
        });
        return doc;
    }

    private static JsonNode B(string json) => JsonNode.Parse(json)!;

    // ---- data.table -----------------------------------------------------------------------

    [Fact]
    public void Table_over_an_entity_source_validates()
    {
        var doc = App(B("""{ "kind":"table","source":{"entity":"booking"},"fields":["purpose","day"] }"""));
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Table_rejects_an_unknown_column()
    {
        var doc = App(B("""{ "kind":"table","source":{"entity":"booking"},"fields":["nope"] }"""));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("table column 'nope'"));
    }

    [Fact]
    public void Table_rejects_a_non_entity_axis()
    {
        // A dates axis yields buckets, which have no columns to tabulate.
        var doc = App(B("""{ "kind":"table","source":{"dates":{"from":"{{today}}","count":7}} }"""));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("table needs an ENTITY source"));
    }

    // ---- data.calendar --------------------------------------------------------------------

    [Fact]
    public void Calendar_over_a_date_field_validates()
    {
        var doc = App(B("""
        { "kind":"calendar","source":{"entity":"booking"},"startField":"day",
          "labelField":"purpose","colorField":"slot" }
        """));
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Calendar_requires_its_start_field_to_be_a_date()
    {
        var doc = App(B("""{ "kind":"calendar","source":{"entity":"booking"},"startField":"purpose" }"""));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("calendar startField") && e.Contains("must be a date field"));
    }

    [Fact]
    public void Calendar_requires_its_color_field_to_be_a_select()
    {
        var doc = App(B("""
        { "kind":"calendar","source":{"entity":"booking"},"startField":"day","colorField":"purpose" }
        """));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("calendar colorField") && e.Contains("must be a select field"));
    }

    // ---- domain.form ----------------------------------------------------------------------

    [Fact]
    public void Form_over_a_known_entity_validates()
    {
        var doc = App(B("""{ "kind":"form","entity":"booking" }"""));
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Form_rejects_an_unknown_entity()
    {
        var doc = App(B("""{ "kind":"form","entity":"ghost" }"""));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("form entity 'ghost' is unknown"));
    }

    // ---- layout.split ---------------------------------------------------------------------

    [Fact]
    public void Split_binds_its_detail_pane_to_the_selected_record()
    {
        // The detail pane's leaves resolve against the LIST's entity — that is the whole point of
        // the item binding, and the thing that would silently break if it were bound to the page.
        var doc = App(B("""
        { "kind":"split","source":{"entity":"booking"},"fields":["day"],
          "blocks":[ { "kind":"field","field":"purpose" } ] }
        """));
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Split_detail_pane_rejects_a_field_the_list_entity_does_not_have()
    {
        var doc = App(B("""
        { "kind":"split","source":{"entity":"booking"},
          "blocks":[ { "kind":"field","field":"not_a_field" } ] }
        """));
        Assert.Contains(Gate.Validate(doc), e => e.Contains("not_a_field"));
    }

    [Fact]
    public void Split_supports_a_relation_hop_in_its_detail_pane()
    {
        var doc = App(B("""
        { "kind":"split","source":{"entity":"booking"},
          "blocks":[ { "kind":"field","field":"room.name" } ] }
        """));
        Assert.Empty(Gate.Validate(doc));
    }

    // ---- authoring names ------------------------------------------------------------------

    [Fact]
    public void All_four_are_authorable_under_their_family()
    {
        Assert.Equal("table", BlockKinds.Find("data.table")!.Canonical);
        Assert.Equal("calendar", BlockKinds.Find("data.calendar")!.Canonical);
        Assert.Equal("form", BlockKinds.Find("domain.form")!.Canonical);
        Assert.Equal("split", BlockKinds.Find("layout.split")!.Canonical);
    }

    [Fact]
    public void A_dotted_authoring_document_lowers_into_a_valid_one()
    {
        var doc = App(
            B("""{ "kind":"data.table","source":{"entity":"booking"},"fields":["purpose"] }"""),
            B("""{ "kind":"data.calendar","source":{"entity":"booking"},"startField":"day" }"""),
            B("""{ "kind":"domain.form","entity":"booking" }"""),
            B("""{ "kind":"layout.split","source":{"entity":"booking"},"blocks":[{"kind":"display.field","field":"purpose"}] }"""));

        Assert.Empty(Gate.StructuralErrors(doc.DeepClone(), Schemas.AuthoringSchema()));
        Assert.Empty(Gate.Validate(Normalizer.LowerAuthoringKinds(doc)));
    }
}
