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
      "entities": [
        {
          "key": "deal",
          "label": "Deal",
          "labelPlural": "Deals",
          "fields": [
            { "key": "title", "label": "Title", "type": "text", "placeholder": "Acme renewal" },
            { "key": "stage", "label": "Stage", "type": "select",
              "options": [
                { "value": "lead", "label": "Lead" },
                { "value": "won", "label": "Won" }
              ] }
          ]
        }
      ],
      "commands": [
        { "key": "win_deal", "label": "Mark as won", "entity": "deal",
          "successMessage": "Deal won" }
      ]
    }
    """)!;

    [Fact]
    public void Extracts_display_text_and_nothing_else()
    {
        var paths = DefinitionText.Extract(Sample()).ToDictionary(e => e.Path, e => e.Text);

        Assert.Equal("Sales CRM", paths["name"]);
        Assert.Equal("Deal", paths["entities/0/label"]);
        Assert.Equal("Deals", paths["entities/0/labelPlural"]);
        Assert.Equal("Title", paths["entities/0/fields/0/label"]);
        Assert.Equal("Acme renewal", paths["entities/0/fields/0/placeholder"]);
        Assert.Equal("Lead", paths["entities/0/fields/1/options/0/label"]);
        Assert.Equal("Mark as won", paths["commands/0/label"]);
        Assert.Equal("Deal won", paths["commands/0/successMessage"]);
    }

    [Fact]
    public void Never_extracts_a_stored_code()
    {
        var paths = DefinitionText.Extract(Sample()).Select(e => e.Path).ToHashSet();

        Assert.DoesNotContain("key", paths);
        Assert.DoesNotContain("entities/0/key", paths);
        Assert.DoesNotContain("entities/0/fields/1/options/0/value", paths);
        Assert.DoesNotContain("entities/0/fields/0/type", paths);
        Assert.DoesNotContain("commands/0/entity", paths);
        Assert.DoesNotContain("commands/0/key", paths);
    }

    [Fact]
    public void Name_is_text_only_at_the_root()
    {
        var doc = JsonNode.Parse("""
        { "name": "Sales CRM", "integrations": [ { "name": "primary-smtp", "type": "smtp" } ] }
        """)!;
        var paths = DefinitionText.Extract(doc).Select(e => e.Path).ToHashSet();

        Assert.Contains("name", paths);
        Assert.DoesNotContain("integrations/0/name", paths);
    }

    [Fact]
    public void Apply_replaces_only_the_paths_the_bundle_names()
    {
        var (doc, applied, skipped) = DefinitionText.Apply(Sample(), new Dictionary<string, string>
        {
            ["entities/0/label"] = "Geschäft",
            ["entities/0/fields/1/options/0/label"] = "Interessent",
        });

        Assert.Equal(2, applied);
        Assert.Equal(0, skipped);
        Assert.Equal("Geschäft", doc["entities"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Interessent", doc["entities"]![0]!["fields"]![1]!["options"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Deals", doc["entities"]![0]!["labelPlural"]!.GetValue<string>());
        Assert.Equal("lead", doc["entities"]![0]!["fields"]![1]!["options"]![0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_does_not_mutate_the_source_document()
    {
        var source = Sample();
        DefinitionText.Apply(source, new Dictionary<string, string> { ["entities/0/label"] = "Geschäft" });
        Assert.Equal("Deal", source["entities"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public void A_stale_bundle_cannot_invent_structure()
    {
        var (doc, applied, skipped) = DefinitionText.Apply(Sample(), new Dictionary<string, string>
        {
            ["entities/9/label"] = "Nicht da",
            ["entities/0/nonexistent"] = "Erfunden",
            ["entities/0/fields"] = "Ein Satz",
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
            ["entities/0/label"] = "Geschäft",
            ["entities/0/gone"] = "Verwaist",
        };
        var sources = new Dictionary<string, string>
        {
            ["entities/0/label"] = "Opportunity",
        };

        var (missing, stale, orphaned) = DefinitionText.Diff(Sample(), bundle, sources);

        Assert.Contains("entities/0/labelPlural", missing);
        Assert.Contains("commands/0/successMessage", missing);
        Assert.Contains("entities/0/label", stale);
        Assert.Contains("entities/0/gone", orphaned);
    }

    [Fact]
    public void Blank_text_is_not_extracted_and_not_applied()
    {
        var doc = JsonNode.Parse("""{ "name": "App", "entities": [ { "key": "a", "label": "   " } ] }""")!;
        Assert.DoesNotContain("entities/0/label", DefinitionText.Extract(doc).Select(e => e.Path));

        var (_, applied, skipped) = DefinitionText.Apply(doc, new Dictionary<string, string> { ["name"] = "  " });
        Assert.Equal(0, applied);
        Assert.Equal(1, skipped);
    }
}
