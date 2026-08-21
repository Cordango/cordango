// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>Deterministic completion of a finished design. Every case here is drawn from the
/// 2026-08-02 MeetingPrep app, whose home page had a New button and a planned calendar and rendered
/// neither, because both sat behind a screen-state toggle that was false when the page loaded.</summary>
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

    /// <summary>The shipped shape: `mode` defaults to "week", and the only surface that could create
    /// a meeting is the table view behind `mode == "list"`.</summary>
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

    // ---- create path ----------------------------------------------------------------------------

    [Fact]
    public void An_app_with_no_create_path_anywhere_gets_one()
    {
        // The floor. Strip the only surface that could create a meeting and the app becomes a
        // read-only viewer over records nobody can add.
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
        // Not a technicality: BlockRenderer wires calendar/timeline/dashboard views to `open` only,
        // never to `create`. A page whose sole surface is a calendar genuinely has no create path,
        // which is precisely the surface a scheduling app reaches for.
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "calendar" } ]""");

        Assert.Contains(DesignDefaults.Apply(Domain(), Plan(), merged), n => n.Contains("no create path"));
    }

    [Fact]
    public void A_table_gets_its_own_New_button_rather_than_a_stray_one()
    {
        // Switching on the list's own button reads as part of the list; a separate button above it
        // would be a second affordance for the same thing.
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
        // Deliberate, and the corpus forced it: sales-crm parks the activity table in a THIRD tab.
        // A tab and a segmented toggle are the same affordance — one labelled click — so calling one
        // reachable and the other not would be a distinction with no basis in what a user can do.
        // The cost is that this fill alone does not answer "the default surface has no New button";
        // doctrine does.
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
        // No plan, so the primary view is not placed either — the page really shows nothing.
        Assert.DoesNotContain(DesignDefaults.Apply(Domain(), plan: null, merged),
            n => n.Contains("create path"));
    }

    [Fact]
    public void Creating_is_an_app_wide_property_not_a_per_page_one()
    {
        // sales-crm's "My Day" lists deals and deliberately has NO New button, because the Deals
        // page owns creating them. Asking the question per page rewrites apps like that.
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

    // ---- primary view placement -----------------------------------------------------------------

    [Fact]
    public void A_page_rendering_none_of_its_planned_views_gets_its_primary_placed()
    {
        // The calendar the plan made this page's first surface, the domain declared correctly, and
        // the screen designer replaced with a hand-rolled grid it never linked back to.
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
        // "Planned" is not "mandatory": a page that renders one planned view and omits another has
        // made a decision. Only a page showing NONE of them is broken.
        var merged = MergedWithHiddenList();
        PageOf(merged)["blocks"] = JsonNode.Parse("""[ { "kind": "view", "view": "list" } ]""");

        DesignDefaults.Apply(Domain(), Plan(), merged);

        Assert.DoesNotContain(PageOf(merged)["blocks"]!.AsArray(),
            b => b!["kind"]!.GetValue<string>() == "section");
    }

    [Fact]
    public void Only_the_first_tab_counts_as_visible_for_view_placement()
    {
        // Placement asks what is on screen at load, and a surface behind a tab is not. (The create
        // floor asks a different question — see A_create_path_behind_a_tab_or_toggle_still_counts.)
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
        // Refinement and imported apps have no plan document. The create floor reads the definition
        // alone, so it still holds; only primary-view placement needs the plan's priority order.
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

/// <summary>Completion must be invisible on documents that were already whole — a fill is a repair,
/// never a house style imposed on hand-authored apps.</summary>
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
