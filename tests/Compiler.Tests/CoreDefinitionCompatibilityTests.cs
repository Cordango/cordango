// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class CoreDefinitionCompatibilityTests
{
    private static JsonObject V1() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "core_thing", "name": "Things", "version": "1.0.0",
      "entities": [
        { "key": "thing", "label": "Thing", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "amount", "label": "Amount", "type": "number" },
            { "key": "status", "label": "Status", "type": "select",
              "options": [{ "value": "active" }, { "value": "archived" }] },
            { "key": "owner", "label": "Owner", "type": "reference",
              "targetEntity": "person", "targetApp": "platform", "onDelete": "setNull" }
          ] }
      ]
    }
    """)!;

    private static JsonObject Mutate(Action<JsonObject> change)
    {
        var doc = V1();
        change(doc);
        return doc;
    }

    private static JsonObject Field(JsonObject doc, string key) =>
        (JsonObject)((JsonArray)doc["entities"]![0]!["fields"]!).First(f => (string)f!["key"]! == key)!;

    private static IReadOnlyList<string> Breaking(JsonObject next) =>
        CoreDefinitionCompatibility.Check(V1(), next).Breaking;

    [Fact]
    public void Identical_definitions_are_compatible() =>
        Assert.True(CoreDefinitionCompatibility.Check(V1(), V1()).Ok);

    [Fact]
    public void Adding_a_field_and_an_entity_is_compatible()
    {
        var next = Mutate(d =>
        {
            ((JsonArray)d["entities"]![0]!["fields"]!).Add(JsonNode.Parse(
                """{ "key": "note", "label": "Note", "type": "longtext" }"""));
            ((JsonArray)d["entities"]!).Add(JsonNode.Parse("""
                { "key": "extra", "label": "Extra", "displayField": "name",
                  "fields": [{ "key": "name", "label": "Name", "type": "text" }] }
            """));
        });
        Assert.True(CoreDefinitionCompatibility.Check(V1(), next).Ok);
    }

    [Fact]
    public void Adding_a_select_option_is_compatible()
    {
        var next = Mutate(d => ((JsonArray)Field(d, "status")["options"]!)
            .Add(JsonNode.Parse("""{ "value": "merged" }""")));
        Assert.True(CoreDefinitionCompatibility.Check(V1(), next).Ok);
    }

    [Fact]
    public void Changing_a_field_type_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "amount")["type"] = "money")),
            e => e.Contains("type changed") && e.Contains("amount"));

    [Fact]
    public void Removing_a_field_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d =>
                ((JsonArray)d["entities"]![0]!["fields"]!).RemoveAt(1))),
            e => e.Contains("amount") && e.Contains("removed or renamed"));

    [Fact]
    public void Renaming_a_field_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "name")["key"] = "title")),
            e => e.Contains("'thing.name'") && e.Contains("removed or renamed"));

    [Fact]
    public void Removing_an_entity_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => ((JsonArray)d["entities"]!).RemoveAt(0))),
            e => e.Contains("entity 'thing'") && e.Contains("removed or renamed"));

    [Fact]
    public void Making_a_field_required_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "amount")["required"] = true)),
            e => e.Contains("became required"));

    [Fact]
    public void Making_a_field_unique_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "name")["unique"] = true)),
            e => e.Contains("became unique"));

    [Fact]
    public void Removing_a_select_option_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => ((JsonArray)Field(d, "status")["options"]!).RemoveAt(1))),
            e => e.Contains("option 'archived'"));

    [Fact]
    public void Repointing_a_reference_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "owner")["targetEntity"] = "group")),
            e => e.Contains("targetEntity changed"));

    [Fact]
    public void Changing_on_delete_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => Field(d, "owner")["onDelete"] = "restrict")),
            e => e.Contains("onDelete changed"));

    [Fact]
    public void Changing_the_app_key_is_breaking() =>
        Assert.Contains(Breaking(Mutate(d => d["key"] = "core_things")),
            e => e.Contains("app key changed"));

    [Fact]
    public void Changing_a_default_is_flagged_for_review_but_not_breaking()
    {
        var report = CoreDefinitionCompatibility.Check(V1(), Mutate(d => Field(d, "status")["default"] = "active"));
        Assert.True(report.Ok);
        Assert.Contains(report.Review, e => e.Contains("default changed"));
    }

    [Fact]
    public void Every_shipped_core_version_upgrades_cleanly_to_the_current_one()
    {
        foreach (var app in CoreAppRegistry.All)
        {
            var current = app.Current;
            foreach (var previous in app.Versions.Where(v => v.Version != current.Version))
            {
                var report = CoreDefinitionCompatibility.Check(previous.Node(), current.Node());
                Assert.True(report.Ok,
                    $"{app.SystemKey} {previous.Version} -> {current.Version} is not upgradable:\n"
                    + string.Join("\n", report.Breaking));
            }
        }
    }

    [Fact]
    public void Core_versions_are_distinct_and_the_current_one_is_last()
    {
        foreach (var app in CoreAppRegistry.All)
        {
            var versions = app.Versions.Select(v => v.Version).ToList();
            Assert.Equal(versions.Distinct().Count(), versions.Count);
            Assert.Equal(versions[^1], app.Current.Version);
        }
    }

    [Fact]
    public void Every_core_definition_passes_the_gate()
    {
        foreach (var app in CoreAppRegistry.All)
        foreach (var version in app.Versions)
            Assert.Empty(Gate.Validate(version.Node()));
    }

    [Fact]
    public void Default_role_exists_in_the_current_definition()
    {
        foreach (var app in CoreAppRegistry.All.Where(a => a.AllMembersRead))
        {
            var roles = app.Current.Node()["roles"] as JsonArray;
            Assert.NotNull(roles);
            Assert.Contains(roles!, r => (string?)r?["key"] == app.DefaultRole);
        }
    }
}
