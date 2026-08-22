// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class BlockKindsTests
{
    private static HashSet<string> CanonicalKinds(string def = "block")
    {
        var schema = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!;
        var en = (JsonArray)schema["$defs"]![def]!["properties"]!["kind"]!["enum"]!;
        return en.Select(x => x!.GetValue<string>()).ToHashSet();
    }

    private static List<string> AuthoringEnum(string def = "block")
    {
        var en = (JsonArray)Schemas.AuthoringSchema()["$defs"]![def]!["properties"]!["kind"]!["enum"]!;
        return en.Select(x => x!.GetValue<string>()).ToList();
    }

    [Fact]
    public void Every_authoring_name_lowers_to_a_real_canonical_kind()
    {
        var canonical = CanonicalKinds();
        foreach (var k in BlockKinds.All)
            Assert.True(canonical.Contains(k.Canonical),
                $"{k.Name} lowers to '{k.Canonical}', which is not in the schema's block kind enum");
    }

    [Fact]
    public void Every_canonical_kind_is_authorable_except_the_deprecated_ones()
    {
        foreach (var canonical in CanonicalKinds())
        {
            if (BlockKinds.NotAuthorable.Contains(canonical))
            {
                Assert.False(BlockKinds.ByCanonical.ContainsKey(canonical),
                    $"'{canonical}' is deprecated at authoring but still has an authoring name");
                continue;
            }
            Assert.True(BlockKinds.ByCanonical.ContainsKey(canonical),
                $"canonical kind '{canonical}' has no authoring name — the model cannot emit it");
        }
    }

    [Fact]
    public void Authoring_names_are_dotted_and_use_a_declared_family()
    {
        foreach (var k in BlockKinds.All)
        {
            Assert.Contains('.', k.Name);
            Assert.StartsWith(k.Family + ".", k.Name);
            Assert.Contains(k.Family, BlockKinds.Families);
        }
    }

    [Fact]
    public void Only_control_carries_a_preset_discriminator()
    {
        foreach (var k in BlockKinds.All.Where(k => k.PresetProperty is not null))
        {
            Assert.Equal("control", k.Canonical);
            Assert.Equal("control", k.PresetProperty);
            Assert.False(string.IsNullOrEmpty(k.PresetValue));
        }
        Assert.Equal(2, BlockKinds.ByCanonical["control"].Count);
    }

    [Fact]
    public void Lowering_rewrites_a_nested_block_tree_to_canonical_kinds()
    {
        var doc = JsonNode.Parse("""
        { "pages": [ { "key": "home", "label": "Home", "blocks": [
            { "kind": "layout.row", "blocks": [
              { "kind": "display.text", "value": "hi" },
              { "kind": "data.repeat", "as": "r", "blocks": [
                { "kind": "data.cell", "field": "level" } ] } ] } ] } ] }
        """)!;

        Normalizer.LowerAuthoringKinds(doc);

        var row = doc["pages"]![0]!["blocks"]![0]!;
        Assert.Equal("row", row["kind"]!.GetValue<string>());
        Assert.Equal("text", row["blocks"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("repeat", row["blocks"]![1]!["kind"]!.GetValue<string>());
        Assert.Equal("cell", row["blocks"]![1]!["blocks"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Lowering_a_control_sets_its_discriminator()
    {
        var doc = JsonNode.Parse("""
        { "blocks": [ { "kind": "control.stepper", "stateKey": "cursor" },
                      { "kind": "control.segmented", "stateKey": "mode" } ] }
        """)!;

        Normalizer.LowerAuthoringKinds(doc);

        Assert.Equal("control", doc["blocks"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("stepper", doc["blocks"]![0]!["control"]!.GetValue<string>());
        Assert.Equal("control", doc["blocks"]![1]!["kind"]!.GetValue<string>());
        Assert.Equal("segmented", doc["blocks"]![1]!["control"]!.GetValue<string>());
    }

    [Fact]
    public void Lowering_leaves_canonical_and_unknown_kinds_alone()
    {
        var doc = JsonNode.Parse("""
        { "blocks": [ { "kind": "row" }, { "kind": "totally_made_up" }, { "kind": "widgets" } ] }
        """)!;

        Normalizer.LowerAuthoringKinds(doc);

        Assert.Equal("row", doc["blocks"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("totally_made_up", doc["blocks"]![1]!["kind"]!.GetValue<string>());
        Assert.Equal("widgets", doc["blocks"]![2]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Lowering_does_not_touch_other_uses_of_the_key_kind()
    {
        var doc = JsonNode.Parse("""
        { "archetype": { "kind": "scheduling", "coreJob": "x" },
          "entities": [ { "key": "e", "kind": "config" } ] }
        """)!;

        Normalizer.LowerAuthoringKinds(doc);

        Assert.Equal("scheduling", doc["archetype"]!["kind"]!.GetValue<string>());
        Assert.Equal("config", doc["entities"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Authoring_schema_exposes_only_namespaced_kinds()
    {
        var names = AuthoringEnum();
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.Contains('.', n));
        Assert.Equal(BlockKinds.Names.OrderBy(x => x), names.OrderBy(x => x));
    }

    [Fact]
    public void Authoring_schema_omits_kinds_deprecated_at_authoring()
    {
        var names = AuthoringEnum();
        foreach (var dead in BlockKinds.NotAuthorable)
            Assert.DoesNotContain(names, n => n == dead || n.EndsWith("." + dead, StringComparison.Ordinal));
    }

    [Fact]
    public void Authoring_schema_namespaces_form_blocks_too()
    {
        Assert.Equal(["layout.section", "display.fields"], AuthoringEnum("formBlock"));
    }

    [Fact]
    public void Authoring_schema_splits_control_into_one_variant_per_preset()
    {
        var defs = Schemas.AuthoringSchema()["$defs"]!.AsObject();
        foreach (var (name, alsoRequires) in new[]
                 { ("block_control_segmented", (string?)null), ("block_control_stepper", "step") })
        {
            var v = defs[name];
            Assert.True(v is not null, $"{name} missing from the authoring schema");
            Assert.Null(v!["properties"]!["control"]);
            var required = ((JsonArray)v["required"]!).Select(x => x!.GetValue<string>()).ToList();
            Assert.Contains("stateKey", required);
            Assert.DoesNotContain("control", required);
            if (alsoRequires is not null) Assert.Contains(alsoRequires, required);
        }
    }

    [Fact]
    public void Authoring_schema_drops_variants_the_model_may_not_author()
    {
        var defs = Schemas.AuthoringSchema()["$defs"]!.AsObject();
        foreach (var dead in BlockKinds.NotAuthorable)
            Assert.False(defs.ContainsKey($"block_{dead}"), $"block_{dead} is still reachable by the model");
        Assert.DoesNotContain(defs.Select(kv => kv.Key),
            n => n.StartsWith("block_", StringComparison.Ordinal)
                 && !BlockKinds.All.Any(k => "block_" + k.Name.Replace('.', '_') == n));
    }

    [Fact]
    public void The_stored_schema_is_untouched_so_existing_apps_keep_validating()
    {
        var canonical = CanonicalKinds();
        Assert.Contains("row", canonical);
        Assert.Contains("widgets", canonical);
        Assert.All(canonical, k => Assert.DoesNotContain('.', k));
    }

    [Fact]
    public void Every_block_catalog_entry_advertises_the_kind_the_model_should_write()
    {
        var components = (JsonArray)JsonNode.Parse(ComponentCatalog.Json)!["components"]!;
        var blocks = components.Where(c => c!["tier"]!.GetValue<string>() == "block").ToList();
        Assert.NotEmpty(blocks);

        foreach (var c in blocks)
        {
            var id = c!["id"]!.GetValue<string>();
            var canonical = id["block.".Length..];

            if (BlockKinds.NotAuthorable.Contains(canonical))
            {
                Assert.Null(c["kind"]);
                Assert.True(c["deprecated"]!.GetValue<bool>(), $"{id} should be marked deprecated");
                continue;
            }

            var kind = c["kind"]?.GetValue<string>();
            Assert.True(kind is not null, $"{id} does not tell the model which kind to write");
            Assert.True(BlockKinds.IsAuthoringName(kind), $"{id} advertises unknown kind '{kind}'");
            Assert.Equal(canonical, BlockKinds.Find(kind!)!.Canonical);
        }
    }

    [Fact]
    public void A_dotted_document_validates_authoring_then_lowers_into_a_valid_canonical_one()
    {
        var doc = JsonNode.Parse("""
        {
          "schemaVersion": "2.0", "key": "app", "name": "App", "version": "1.0.0",
          "entities": [ { "key": "thing", "label": "Thing", "displayField": "name",
            "fields": [ { "key": "name", "label": "Name", "type": "text" } ] } ],
          "pages": [ { "key": "home", "label": "Home", "blocks": [
            { "kind": "layout.card", "label": "Recent", "blocks": [
              { "kind": "data.repeat", "source": { "entity": "thing" }, "blocks": [
                { "kind": "display.field", "field": "name" } ] } ] } ] } ]
        }
        """)!;

        Assert.Empty(Gate.StructuralErrors(doc.DeepClone(), Schemas.AuthoringSchema()));

        Assert.NotEmpty(Gate.StructuralErrors(doc.DeepClone(), Schemas.AppDefinitionSchemaNode()));

        var lowered = Normalizer.LowerAuthoringKinds(doc.DeepClone())!;
        Assert.Empty(Gate.StructuralErrors(lowered, Schemas.AppDefinitionSchemaNode()));
        Assert.Empty(Gate.Validate(lowered));
    }
}
