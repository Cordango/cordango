// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;
using Json.Schema;

namespace Cordango.Compiler.Tests;

public class ComponentCatalogTests
{
    [Fact]
    public void Ids_are_unique()
    {
        var ids = ComponentCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Id_prefix_matches_tier()
    {
        foreach (var c in ComponentCatalog.All)
        {
            var prefix = c.Id.Split('.', 2)[0];
            Assert.Equal(c.Tier.ToString().ToLowerInvariant(), prefix);
        }
    }

    [Fact]
    public void Every_config_schema_compiles_and_is_an_object_schema()
    {
        foreach (var c in ComponentCatalog.All)
        {
            var schema = JsonSchema.FromText(c.ConfigSchema.ToJsonString());
            Assert.Equal("object", c.ConfigSchema["type"]?.GetValue<string>());
            Assert.NotNull(schema);
        }
    }

    [Fact]
    public void Config_schemas_are_enforceable_additionalProperties_false()
    {
        var table = JsonSchema.FromText(ComponentCatalog.Find("view.table")!.ConfigSchema.ToJsonString());
        Assert.False(table.Evaluate(Json("""{ "bogus": true }""")).IsValid);
        Assert.True(table.Evaluate(Json("""{ "columns": ["name", "stage"], "density": "compact" }""")).IsValid);
    }

    [Fact]
    public void Widget_aggregate_schema_resolves_its_local_ref()
    {
        var metric = JsonSchema.FromText(ComponentCatalog.Find("widget.metric")!.ConfigSchema.ToJsonString());
        Assert.True(metric.Evaluate(Json("""{ "type": "metric", "source": { "entity": "order", "aggregate": { "op": "sum", "field": "total" } } }""")).IsValid);
        Assert.False(metric.Evaluate(Json("""{ "type": "metric", "source": { "entity": "order", "aggregate": { "op": "bogus" } } }""")).IsValid);
        Assert.False(metric.Evaluate(Json("""{ "source": { "entity": "order", "aggregate": { "op": "count" } } }""")).IsValid);
    }

    [Fact]
    public void Bindings_are_emitted_for_views_widgets_and_blocks()
    {
        var json = JsonNode.Parse(ComponentCatalog.Json)!;
        foreach (var c in ((JsonArray)json["components"]!).OfType<JsonObject>())
        {
            var tier = c["tier"]!.GetValue<string>();
            if (tier is "view" or "widget" or "block")
                Assert.True(c["bindings"] is JsonArray { Count: > 0 }, $"{c["id"]} must declare bindings");
        }
    }

    [Fact]
    public void Forms_capability_components_exist_and_are_module_gated()
    {
        foreach (var id in new[] { "capability.form_designer", "capability.form_runner", "capability.form_results" })
        {
            var c = ComponentCatalog.Find(id);
            Assert.NotNull(c);
            Assert.Equal("forms", c!.Requires?.Module);
        }
    }

    [Fact]
    public void Capabilities_declare_a_module_gate()
    {
        foreach (var c in ComponentCatalog.All.Where(c => c.Tier == ComponentTier.Capability))
            Assert.False(string.IsNullOrEmpty(c.Requires?.Module), $"{c.Id} must declare requires.module");
    }

    [Fact]
    public void Board_requires_a_groupable_field_not_specifically_a_status()
    {
        var board = ComponentCatalog.Find("view.kanban");
        Assert.NotNull(board);
        var shapes = board!.Requires?.DataShape ?? [];
        Assert.Contains(shapes, d => (d.FieldTypes ?? []).Contains("select") && (d.FieldTypes ?? []).Contains("reference"));
        Assert.DoesNotContain(shapes, d => d.Role == "status");
    }

    [Fact]
    public void Calendar_and_timeline_require_a_date_field()
    {
        foreach (var id in new[] { "view.calendar", "view.timeline" })
        {
            var shapes = ComponentCatalog.Find(id)!.Requires?.DataShape ?? [];
            Assert.Contains(shapes, d => d.FieldTypes is { } t && t.Contains("date") && t.Contains("datetime"));
        }
    }

    [Fact]
    public void Nesting_blocks_declare_slots()
    {
        foreach (var id in new[] { "block.tabs", "block.section", "block.columns" })
            Assert.NotEmpty(ComponentCatalog.Find(id)!.Slots ?? []);
    }

    [Fact]
    public void FieldTypes_are_sourced_from_the_app_definition_enum()
    {
        Assert.NotEmpty(ComponentCatalog.FieldTypes);
        Assert.Contains("select", ComponentCatalog.FieldTypes);
        Assert.Contains("reference", ComponentCatalog.FieldTypes);
        Assert.Contains("attachment", ComponentCatalog.FieldTypes);
    }

    [Fact]
    public void Emitted_json_carries_version_and_every_component()
    {
        var json = JsonNode.Parse(ComponentCatalog.Json)!;
        Assert.Equal(ComponentCatalog.Version, json["version"]!.GetValue<string>());
        var emittedIds = ((JsonArray)json["components"]!).Select(c => c!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(ComponentCatalog.All.Select(c => c.Id).ToHashSet(), emittedIds);
        var aiAssistant = ((JsonArray)json["components"]!).First(c => c!["id"]!.GetValue<string>() == "capability.ai_assistant")!;
        Assert.Equal("capability", aiAssistant["tier"]!.GetValue<string>());
        Assert.Equal("ai", aiAssistant["requires"]!["module"]!.GetValue<string>());
    }

    private static JsonElement Json(string s) => JsonNode.Parse(s)!.Deserialize<JsonElement>();
}
