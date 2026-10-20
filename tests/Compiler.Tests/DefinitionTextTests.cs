// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class DefinitionTextTests
{
    private static JsonNode Sample() => JsonNode.Parse("""
    {
      "key": "sales_crm",
      "name": "Sales CRM",
      "purpose": { "summary": "Track deals" },
      "entities": [
        {
          "key": "deal",
          "label": "Deal",
          "labelPlural": "Deals",
          "fields": [
            { "key": "title", "label": "Title", "type": "text", "placeholder": "Acme renewal",
              "help": "What the customer calls it", "group": "Basics" },
            { "key": "stage", "label": "Stage", "type": "select",
              "options": [
                { "value": "lead", "label": "Lead" },
                { "value": "won", "label": "Won" },
                { "value": "n/a", "label": "Open" }
              ] },
            { "key": "size", "label": "Size", "type": "select", "options": [ { "label": "Large" } ] },
            { "key": "tags", "label": "Tags", "type": "multiselect", "options": [ "Hot", "Cold" ] }
          ]
        }
      ],
      "pages": [ { "key": "settings", "label": "Settings", "group": "config" } ],
      "roles": [ { "key": "admin", "name": "Administrator" } ],
      "integrations": [ { "name": "primary-smtp", "type": "smtp" } ],
      "commands": [
        { "key": "win_deal", "label": "Mark as won", "entity": "deal",
          "successMessage": "Deal won", "confirmLabel": "Win it" },
        { "key": "open", "label": "Open", "entity": "deal" }
      ]
    }
    """)!;

    private static Dictionary<string, string> Paths(JsonNode doc) =>
        DefinitionText.Extract(doc).ToDictionary(e => e.Path, e => e.Text);

    private static AppTextBundle Bundle(Dictionary<string, string> text, Dictionary<string, string>? at = null) =>
        new(text, at ?? []);

    [Fact]
    public void Extracts_display_text_by_key_path()
    {
        var paths = Paths(Sample());

        Assert.Equal("Sales CRM", paths["name"]);
        Assert.Equal("Deal", paths["entities/deal/label"]);
        Assert.Equal("Deals", paths["entities/deal/labelPlural"]);
        Assert.Equal("Title", paths["entities/deal/fields/title/label"]);
        Assert.Equal("Acme renewal", paths["entities/deal/fields/title/placeholder"]);
        Assert.Equal("What the customer calls it", paths["entities/deal/fields/title/help"]);
        Assert.Equal("Basics", paths["entities/deal/fields/title/group"]);
        Assert.Equal("Lead", paths["entities/deal/fields/stage/options/lead/label"]);
        Assert.Equal("Open", paths["entities/deal/fields/stage/options/n~1a/label"]);
        Assert.Equal("Administrator", paths["roles/admin/name"]);
        Assert.Equal("Mark as won", paths["commands/win_deal/label"]);
        Assert.Equal("Deal won", paths["commands/win_deal/successMessage"]);
        Assert.Equal("Win it", paths["commands/win_deal/confirmLabel"]);
    }

    [Fact]
    public void Never_extracts_a_stored_code()
    {
        var paths = Paths(Sample());

        Assert.DoesNotContain("key", paths.Keys);
        Assert.DoesNotContain("entities/deal/key", paths.Keys);
        Assert.DoesNotContain("entities/deal/fields/stage/options/lead/value", paths.Keys);
        Assert.DoesNotContain("entities/deal/fields/size/options/0/label", paths.Keys);
        Assert.DoesNotContain("Hot", paths.Values);
        Assert.DoesNotContain("pages/settings/group", paths.Keys);
        Assert.DoesNotContain("commands/win_deal/entity", paths.Keys);
    }

    [Fact]
    public void Name_is_text_only_for_the_app_and_its_roles()
    {
        var paths = Paths(Sample());

        Assert.Contains("name", paths.Keys);
        Assert.Contains("roles/admin/name", paths.Keys);
        Assert.DoesNotContain("integrations/0/name", paths.Keys);
    }

    [Fact]
    public void A_block_that_names_a_field_in_a_text_property_is_not_text()
    {
        var doc = JsonNode.Parse("""
        { "pages": [ { "key": "p", "blocks": [
            { "kind": "hub", "title": "name", "label": "Overview" },
            { "kind": "orgchart", "subtitle": "job_title", "title": "Team" },
            { "kind": "stat", "label": "xs" } ] } ] }
        """)!;

        var texts = DefinitionText.Extract(doc).Select(e => e.Text).ToList();

        Assert.Equal(["Overview", "Team", "xs"], texts);
    }

    [Fact]
    public void An_entity_calendar_names_its_title_field()
    {
        var doc = JsonNode.Parse("""
        { "entities": [ { "key": "leave", "label": "Leave", "calendar": { "title": "type", "start": "from" } } ] }
        """)!;

        Assert.Equal(["entities/leave/label"], Paths(doc).Keys);
    }

    [Fact]
    public void A_text_block_and_an_unbound_chip_draw_their_value()
    {
        var doc = JsonNode.Parse("""
        { "pages": [ { "key": "p", "blocks": [
            { "kind": "text", "value": "{count} picked, waiting to ship" },
            { "kind": "chip", "value": "In stock" },
            { "kind": "stat", "value": "total" } ] } ] }
        """)!;

        var texts = DefinitionText.Extract(doc).Select(e => e.Text).ToList();

        Assert.Equal(["{count} picked, waiting to ship", "In stock"], texts);
    }

    [Fact]
    public void Values_an_effect_writes_into_a_record_are_data()
    {
        var doc = JsonNode.Parse("""
        { "commands": [ { "key": "convert", "label": "Convert",
            "effects": [ { "title": "Deal created", "set": { "title": "{{record.title}}" } } ] } ] }
        """)!;

        var paths = Paths(doc);

        Assert.Equal(["commands/convert/label", "commands/convert/effects/0/title"], paths.Keys);
    }

    [Fact]
    public void Builder_sections_are_not_extracted()
    {
        Assert.DoesNotContain("Track deals", Paths(Sample()).Values);
    }

    [Fact]
    public void Translate_replaces_text_and_reports_what_is_left()
    {
        var (doc, untranslated) = DefinitionText.Translate(Sample(), [Bundle(new()
        {
            ["Deal"] = "Deal",
            ["Stage"] = "Phase",
            ["Won"] = "Gewonnen",
        })]);

        Assert.Equal("Phase", doc["entities"]![0]!["fields"]![1]!["label"]!.GetValue<string>());
        Assert.Equal("Gewonnen", doc["entities"]![0]!["fields"]![1]!["options"]![1]!["label"]!.GetValue<string>());
        Assert.Equal("won", doc["entities"]![0]!["fields"]![1]!["options"]![1]!["value"]!.GetValue<string>());
        Assert.Contains(untranslated, e => e.Path == "entities/deal/labelPlural");
        Assert.DoesNotContain(untranslated, e => e.Path == "entities/deal/label");
    }

    [Fact]
    public void Translate_cannot_reach_a_value_that_stores_its_label()
    {
        var (doc, _) = DefinitionText.Translate(Sample(), [Bundle(new() { ["Large"] = "Groß", ["Hot"] = "Heiß" })]);

        Assert.Equal("Large", doc["entities"]![0]!["fields"]![2]!["options"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Hot", doc["entities"]![0]!["fields"]![3]!["options"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_path_override_wins_for_a_homonym()
    {
        var (doc, _) = DefinitionText.Translate(Sample(), [Bundle(
            new() { ["Open"] = "Offen" },
            new() { ["commands/open/label"] = "Öffnen" })]);

        Assert.Equal("Offen", doc["entities"]![0]!["fields"]![1]!["options"]![2]!["label"]!.GetValue<string>());
        Assert.Equal("Öffnen", doc["commands"]![1]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void The_app_layer_wins_over_the_platform_layer()
    {
        var (doc, _) = DefinitionText.Translate(Sample(), [
            Bundle(new() { ["Title"] = "Bezeichnung" }),
            Bundle(new() { ["Title"] = "Titel", ["Deals"] = "Deals" }),
        ]);

        Assert.Equal("Bezeichnung", doc["entities"]![0]!["fields"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Deals", doc["entities"]![0]!["labelPlural"]!.GetValue<string>());
    }

    [Fact]
    public void A_question_made_from_a_label_is_found_through_the_label()
    {
        var doc = JsonNode.Parse("""{ "commands": [ { "key": "c", "confirm": { "title": "Cancel request?" } } ] }""")!;

        var (translated, untranslated) = DefinitionText.Translate(doc, [Bundle(new() { ["Cancel request"] = "Antrag stornieren" })]);

        Assert.Equal("Antrag stornieren?", translated["commands"]![0]!["confirm"]!["title"]!.GetValue<string>());
        Assert.Empty(untranslated);
    }

    [Fact]
    public void Translate_does_not_mutate_the_source_document()
    {
        var source = Sample();
        DefinitionText.Translate(source, [Bundle(new() { ["Deal"] = "Geschäft" })]);
        Assert.Equal("Deal", source["entities"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_replaces_only_the_paths_the_bundle_names()
    {
        var (doc, applied, skipped) = DefinitionText.Apply(Sample(), new Dictionary<string, string>
        {
            ["entities/deal/label"] = "Geschäft",
            ["entities/deal/fields/stage/options/lead/label"] = "Interessent",
            ["entities/deal/fields/stage/options/n~1a/label"] = "Offen",
        });

        Assert.Equal(3, applied);
        Assert.Equal(0, skipped);
        Assert.Equal("Geschäft", doc["entities"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Interessent", doc["entities"]![0]!["fields"]![1]!["options"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Offen", doc["entities"]![0]!["fields"]![1]!["options"]![2]!["label"]!.GetValue<string>());
        Assert.Equal("Deals", doc["entities"]![0]!["labelPlural"]!.GetValue<string>());
    }

    [Fact]
    public void A_stale_bundle_cannot_invent_structure()
    {
        var (doc, applied, skipped) = DefinitionText.Apply(Sample(), new Dictionary<string, string>
        {
            ["entities/ghost/label"] = "Nicht da",
            ["entities/deal/nonexistent"] = "Erfunden",
            ["entities/deal/fields"] = "Ein Satz",
        });

        Assert.Equal(0, applied);
        Assert.Equal(3, skipped);
        Assert.Null(doc["entities"]![0]!["nonexistent"]);
        Assert.IsType<JsonArray>(doc["entities"]![0]!["fields"]);
    }

    [Fact]
    public void Diff_reports_what_needs_work()
    {
        var bundle = new Dictionary<string, string>
        {
            ["entities/deal/label"] = "Geschäft",
            ["entities/deal/gone"] = "Verwaist",
        };
        var sources = new Dictionary<string, string>
        {
            ["entities/deal/label"] = "Opportunity",
        };

        var (missing, stale, orphaned) = DefinitionText.Diff(Sample(), bundle, sources);

        Assert.Contains("entities/deal/labelPlural", missing);
        Assert.Contains("commands/win_deal/successMessage", missing);
        Assert.Contains("entities/deal/label", stale);
        Assert.Contains("entities/deal/gone", orphaned);
    }

    [Fact]
    public void Blank_text_is_not_extracted_and_not_applied()
    {
        var doc = JsonNode.Parse("""{ "name": "App", "entities": [ { "key": "a", "label": "   " } ] }""")!;
        Assert.DoesNotContain("entities/a/label", DefinitionText.Extract(doc).Select(e => e.Path));

        var (_, applied, skipped) = DefinitionText.Apply(doc, new Dictionary<string, string> { ["name"] = "  " });
        Assert.Equal(0, applied);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void A_bundle_reads_text_and_at_and_ignores_the_rest()
    {
        var bundle = AppTextBundle.Parse(JsonNode.Parse("""
        { "locale": "de", "text": { "Deal": "Deal", "Bad": 3 }, "at": { "commands/open/label": "Öffnen" } }
        """));

        Assert.Equal("Deal", bundle.Text["Deal"]);
        Assert.False(bundle.Text.ContainsKey("Bad"));
        Assert.Equal("Öffnen", bundle.At["commands/open/label"]);
    }
}
