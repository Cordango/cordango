// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class DesignDefaultsTests
{
    private static JsonObject Domain() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "meeting", "label": "Meeting", "displayField": "title",
          "form": { "blocks": [ { "kind": "fields", "fields": ["title"] } ] },
          "fields": [
            { "key": "title", "label": "Title", "type": "text" },
            { "key": "start_at", "label": "Start", "type": "datetime" } ] },
        { "key": "app_settings", "label": "Settings", "kind": "settings", "displayField": "name",
          "fields": [ { "key": "name", "label": "Name", "type": "text" } ] }
      ]
    }
    """)!;

    private static JsonObject MergedWithHiddenList() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "meeting", "label": "Meeting", "displayField": "title",
          "form": { "blocks": [ { "kind": "fields", "fields": ["title"] } ] },
          "fields": [
            { "key": "title", "label": "Title", "type": "text" },
            { "key": "start_at", "label": "Start", "type": "datetime" } ] }
      ],
      "views": [
        { "key": "calendar", "label": "Calendar", "type": "calendar", "entity": "meeting",
          "config": { "dateField": "start_at", "titleField": "title" } },
        { "key": "list", "label": "All Meetings", "type": "table", "entity": "meeting",
          "config": { "columns": ["title", "start_at"] } }
      ],
      "pages": [
        { "key": "meeting_schedule", "label": "Meetings", "entity": "meeting",
          "state": [ { "key": "mode", "type": "enum", "default": "week",
                       "options": [ { "value": "week", "label": "Week" }, { "value": "list", "label": "List" } ] } ],
          "blocks": [
            { "kind": "control", "control": "segmented", "stateKey": "mode" },
            { "kind": "repeat", "as": "day", "direction": "row",
              "source": { "dates": { "from": "2026-08-03", "step": "day", "count": 7 } },
              "blocks": [ { "kind": "text", "value": "{{day.weekday}}" } ] },
            { "kind": "view", "view": "list", "visibleWhen": { "value": "{{state.mode}}", "eq": "list" } }
          ] }
      ]
    }
    """)!;

    private static DesignPlan Plan() => DesignPlan.Parse(JsonNode.Parse("""
    {
      "pages": [
        { "key": "meeting_schedule", "label": "Meetings", "role": "home", "entity": "meeting",
          "views": [
            { "key": "calendar", "label": "Calendar", "type": "calendar", "entity": "meeting" },
            { "key": "list", "label": "All Meetings", "type": "table", "entity": "meeting" } ] }
      ],
      "details": [ { "entity": "meeting", "form": true } ],
      "surfaces": []
    }
    """))!;

    private static JsonObject PageOf(JsonObject merged) =>
        merged["pages"]!.AsArray().OfType<JsonObject>().First();

    [Fact]
    public void An_app_with_no_create_path_anywhere_gets_one()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""
            [ { "kind": "view", "view": "calendar" } ]
            """);
        var notes = DesignDefaults.Apply(Domain(), Plan(), merged);

        Assert.Contains(notes, n => n.Contains("no create path"));
        var first = PageOf(merged)["blocks"]!.AsArray()[0]!.AsObject();
        Assert.Equal("create", first["kind"]!.GetValue<string>());
        Assert.Equal("meeting", first["entity"]!.GetValue<string>());
        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void A_calendar_only_page_cannot_create_and_is_treated_as_such()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "calendar" } ]""");

        Assert.Contains(DesignDefaults.Apply(Domain(), Plan(), merged), n => n.Contains("no create path"));
    }

    [Fact]
    public void A_table_gets_its_own_New_button_rather_than_a_stray_one()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""
            [ { "kind": "table", "source": { "entity": "meeting" }, "fields": ["title"] } ]
            """);
        var notes = DesignDefaults.Apply(Domain(), Plan(), merged);

        var table = PageOf(merged)["blocks"]!.AsArray()[0]!.AsObject();
        Assert.True(table["newButton"]!.GetValue<bool>());
        Assert.DoesNotContain(PageOf(merged)["blocks"]!.AsArray(),
            b => b!["kind"]!.GetValue<string>() == "create");
        Assert.Contains(notes, n => n.Contains("enabled the New button"));
    }

    [Fact]
    public void A_create_path_behind_a_tab_or_toggle_still_counts()
    {
        var merged = MergedWithHiddenList();
        var before = merged.ToJsonString();

        Assert.Empty(DesignDefaults.Apply(Domain(), plan: null, merged));
        Assert.Equal(before, merged.ToJsonString());
    }

    [Fact]
    public void A_page_that_already_creates_is_untouched()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""
            [ { "kind": "view", "view": "list" } ]
            """);
        var before = merged.ToJsonString();

        var notes = DesignDefaults.Apply(Domain(), Plan(), merged);

        Assert.Empty(notes);
        Assert.Equal(before, merged.ToJsonString());
    }

    [Fact]
    public void A_page_that_never_shows_the_entity_gets_no_orphan_button()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""
            [ { "kind": "text", "value": "Welcome" } ]
            """);
        Assert.DoesNotContain(DesignDefaults.Apply(Domain(), plan: null, merged),
            n => n.Contains("create path"));
    }

    [Fact]
    public void Creating_is_an_app_wide_property_not_a_per_page_one()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "calendar" } ]""");
        ((JsonArray)merged["pages"]!).Add(JsonNode.Parse("""
            { "key": "all_meetings", "label": "All Meetings", "entity": "meeting",
              "blocks": [ { "kind": "view", "view": "list" } ] }
            """));
        var before = merged.ToJsonString();

        var notes = DesignDefaults.Apply(Domain(), plan: null, merged);

        Assert.Empty(notes);
        Assert.Equal(before, merged.ToJsonString());
    }

    [Fact]
    public void A_singleton_page_gets_no_create_button()
    {
        var merged = MergedWithHiddenList();
        ((JsonArray)merged["entities"]!).Add(JsonNode.Parse("""
            { "key": "app_settings", "label": "Settings", "kind": "settings", "displayField": "name",
              "fields": [ { "key": "name", "label": "Name", "type": "text" } ] }
            """));
        var page = PageOf(merged);
        page["entity"] = "app_settings";
        page["blocks"] = JsonNode.Parse("""[ { "kind": "settings", "entity": "app_settings" } ]""");

        Assert.DoesNotContain(DesignDefaults.Apply(Domain(), Plan(), merged),
            n => n.Contains("create path"));
    }

    [Fact]
    public void A_page_rendering_none_of_its_planned_views_gets_its_primary_placed()
    {
        var merged = MergedWithHiddenList();
        DesignDefaults.Apply(Domain(), Plan(), merged);

        var placed = merged["pages"]![0]!["blocks"]!.AsArray()
            .OfType<JsonObject>()
            .SelectMany(b => b["blocks"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            .Any(b => (string?)b["kind"] == "view" && (string?)b["view"] == "calendar");
        Assert.True(placed, "the planned primary view was not placed");
        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void A_page_showing_a_planned_view_keeps_the_author_s_choice()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "list" } ]""");

        DesignDefaults.Apply(Domain(), Plan(), merged);

        Assert.DoesNotContain(PageOf(merged)["blocks"]!.AsArray(),
            b => b!["kind"]!.GetValue<string>() == "section");
    }

    [Fact]
    public void Only_the_first_tab_counts_as_visible_for_view_placement()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""
            [ { "kind": "tabs", "tabs": [
                { "label": "Week", "blocks": [ { "kind": "text", "value": "grid" } ] },
                { "label": "List", "blocks": [ { "kind": "view", "view": "list" } ] } ] } ]
            """);

        Assert.Contains(DesignDefaults.Apply(Domain(), Plan(), merged),
            n => n.Contains("planned primary view"));
    }

    [Fact]
    public void Completion_without_a_plan_still_guarantees_the_create_floor()
    {
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "calendar" } ]""");

        var notes = DesignDefaults.Apply(Domain(), plan: null, merged);

        Assert.Contains(notes, n => n.Contains("no create path"));
        Assert.DoesNotContain(notes, n => n.Contains("planned primary view"));
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time()
    {
        var merged = MergedWithHiddenList();
        DesignDefaults.Apply(Domain(), Plan(), merged);
        var once = merged.ToJsonString();

        Assert.Empty(DesignDefaults.Apply(Domain(), Plan(), merged));
        Assert.Equal(once, merged.ToJsonString());
    }
}

public class DesignDefaultsCorpusTests
{
    [Theory]
    [MemberData(nameof(ReferenceSuiteTests.AppDefs), MemberType = typeof(ReferenceSuiteTests))]
    public void A_healthy_reference_app_is_left_byte_identical(string file)
    {
        var doc = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)))!;
        var before = doc.ToJsonString();

        var notes = DesignDefaults.Apply(doc, DesignPlan.FromDefinition(doc), doc);

        Assert.Empty(notes);
        Assert.Equal(before, doc.ToJsonString());
    }
}
