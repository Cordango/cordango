// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class SchemaConformanceTests
{

    public static TheoryData<string> MaintainedDefinitions() => Corpus.MaintainedDefinitions();

    public static TheoryData<string> AllDefinitions() => Corpus.AllDefinitions();

    private static JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public void The_corpus_is_found_and_non_trivial()
    {
        Assert.True(MaintainedDefinitions().Count >= 4, "expected the 3 reference apps + crm");
        Assert.True(AllDefinitions().Count >= 18, "expected the full example corpus");
    }

    [Theory]
    [MemberData(nameof(MaintainedDefinitions))]
    public void Maintained_definitions_pass_the_whole_gate(string path)
    {
        var errors = Gate.Validate(Load(path));
        Assert.True(errors.Count == 0,
            $"{Path.GetFileName(path)} no longer passes the gate:\n  " + string.Join("\n  ", errors));
    }

    private static JsonNode DefSchema(string defName)
    {
        var schema = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!;
        return new JsonObject
        {
            ["$ref"] = $"#/$defs/{defName}",
            ["$defs"] = schema["$defs"]!.DeepClone(),
        };
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void Every_block_validates_against_the_block_definition(string path)
    {
        var blockSchema = DefSchema("block");
        var failures = new List<string>();
        foreach (var (block, where) in BlocksOf(Load(path)))
        {
            var errors = Gate.StructuralErrors(block.DeepClone(), blockSchema);
            if (errors.Count > 0)
                failures.Add($"{where} (kind='{block["kind"]?.GetValue<string>()}'): {string.Join("; ", errors)}");
        }
        Assert.True(failures.Count == 0,
            $"{Path.GetFileName(path)}: block(s) rejected by $defs/block:\n  " + string.Join("\n  ", failures));
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void Every_effect_validates_against_the_effect_definition(string path)
    {
        var effectSchema = DefSchema("effect");
        var doc = Load(path);
        var failures = new List<string>();
        void Check(JsonNode? effects, string where)
        {
            if (effects is not JsonArray arr) return;
            foreach (var e in arr)
            {
                if (e is not JsonObject eff) continue;
                var errors = Gate.StructuralErrors(eff.DeepClone(), effectSchema);
                if (errors.Count > 0)
                    failures.Add($"{where} (type='{eff["type"]?.GetValue<string>()}'): {string.Join("; ", errors)}");
            }
        }
        foreach (var c in doc["commands"] as JsonArray ?? []) Check(c?["effects"], $"command '{c?["key"]}'");
        foreach (var w in doc["workflows"] as JsonArray ?? []) Check(w?["effects"], $"workflow '{w?["key"]}'");
        Assert.True(failures.Count == 0,
            $"{Path.GetFileName(path)}: effect(s) rejected by $defs/effect:\n  " + string.Join("\n  ", failures));
    }

    [Theory]
    [MemberData(nameof(AllDefinitions))]
    public void Every_authored_block_is_binding_legal_where_it_was_authored(string path)
    {
        var doc = Load(path);
        var failures = new List<string>();

        void Walk(JsonNode? blocks, string context, string where)
        {
            if (blocks is not JsonArray arr) return;
            foreach (var b in arr)
            {
                if (b is not JsonObject block) continue;
                if (block["kind"]?.GetValue<string>() is { } kind
                    && !ComponentCatalog.BlockIsLegalIn(kind, context))
                    failures.Add($"{where}: '{kind}' is not legal in a {context} — narrowing would delete it");
                Walk(block["blocks"], context, where);
                foreach (var t in block["tabs"] as JsonArray ?? []) Walk(t?["blocks"], context, where);
                foreach (var c in block["columns"] as JsonArray ?? []) Walk(c, context, where);
            }
        }

        foreach (var p in doc["pages"] as JsonArray ?? []) Walk(p?["blocks"], "page", $"page '{p?["key"]}'");
        foreach (var e in doc["entities"] as JsonArray ?? [])
            Walk(e?["detail"]?["blocks"], "detail", $"entity '{e?["key"]}'.detail");

        Assert.True(failures.Count == 0,
            $"{Path.GetFileName(path)}:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void Narrowing_only_ever_drops_what_the_catalog_positively_rules_out()
    {
        foreach (var undeclared in new[] { "control", "settings", "timeline" })
        {
            Assert.Null(ComponentCatalog.Find("block." + undeclared));
            Assert.True(ComponentCatalog.BlockIsLegalIn(undeclared, "page"));
            Assert.True(ComponentCatalog.BlockIsLegalIn(undeclared, "detail"));
        }
        Assert.True(ComponentCatalog.BlockIsLegalIn("hub", "some_future_slice_kind"));
        Assert.False(ComponentCatalog.BlockIsLegalIn("hub", "page"));
        Assert.False(ComponentCatalog.BlockIsLegalIn("view", "detail"));
    }

    private static JsonNode AuthoringDefSchema(string defName)
    {
        var schema = Schemas.AuthoringSchema();
        return new JsonObject
        {
            ["$ref"] = $"#/$defs/{defName}",
            ["$defs"] = schema["$defs"]!.DeepClone(),
        };
    }

    [Theory]
    [MemberData(nameof(MaintainedDefinitions))]
    public void Every_view_config_in_the_corpus_satisfies_its_published_contract(string path)
    {
        var viewSchema = AuthoringDefSchema("view");
        var failures = new List<string>();
        foreach (var v in Load(path)["views"] as JsonArray ?? [])
        {
            if (v is not JsonObject view) continue;
            var errors = Gate.StructuralErrors(view.DeepClone(), viewSchema);
            if (errors.Count > 0)
                failures.Add($"view '{view["key"]}' ({view["type"]}): {string.Join("; ", errors)}");
        }
        Assert.True(failures.Count == 0,
            $"{Path.GetFileName(path)}:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void A_view_config_the_gate_would_reject_is_now_rejected_at_authoring_time()
    {
        var viewSchema = AuthoringDefSchema("view");
        List<string> Check(string json) => Gate.StructuralErrors(JsonNode.Parse(json)!, viewSchema);

        Assert.Empty(Check("""
        { "key":"board","label":"Board","type":"kanban","entity":"deal",
          "config":{"groupByField":"stage","cardFields":["name"]} }
        """));
        Assert.NotEmpty(Check("""
        { "key":"board","label":"Board","type":"kanban","entity":"deal","config":{"banana":true} }
        """));
        Assert.NotEmpty(Check("""
        { "key":"board","label":"Board","type":"kanban","entity":"deal","config":{"dateField":"due_at"} }
        """));
        Assert.NotEmpty(Check("""
        { "key":"cal","label":"Cal","type":"calendar","entity":"deal","config":{"groupByField":"stage"} }
        """));
        Assert.Equal("object",
            JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!["$defs"]!["view"]!["properties"]!["config"]!["type"]!
                .GetValue<string>());
    }

    private static List<string> BlockErrors(string json) =>
        Gate.StructuralErrors(JsonNode.Parse(json)!, DefSchema("block"));

    [Fact]
    public void A_block_carrying_another_kinds_properties_is_rejected()
    {
        Assert.Empty(BlockErrors("""
        { "kind":"card", "blocks":[{"kind":"text","value":"x"}] }
        """));
        Assert.NotEmpty(BlockErrors("""
        { "kind":"card", "command":"approve", "rowBy":"employee", "editable":true,
          "chartType":"pie", "stateKey":"week", "blocks":[{"kind":"text","value":"x"}] }
        """));
    }

    [Fact]
    public void A_clean_block_of_each_shape_still_passes()
    {
        Assert.Empty(BlockErrors("""{ "kind":"card","label":"X","padding":"sm","blocks":[{"kind":"text","value":"x"}] }"""));
        Assert.Empty(BlockErrors("""{ "kind":"field","field":"name","weight":"bold" }"""));
        Assert.Empty(BlockErrors("""{ "kind":"control","control":"segmented","stateKey":"mode" }"""));
        Assert.Empty(BlockErrors("""{ "kind":"stat","source":{"entity":"t","aggregate":{"op":"count"}} }"""));
    }

    [Theory]
    [InlineData("""{ "kind":"stat" }""")]
    [InlineData("""{ "kind":"text" }""")]
    [InlineData("""{ "kind":"chip" }""")]
    [InlineData("""{ "kind":"chart","source":{"entity":"t"} }""")]
    [InlineData("""{ "kind":"control","control":"stepper","stateKey":"c" }""")]
    public void Under_specified_blocks_are_rejected(string json) => Assert.NotEmpty(BlockErrors(json));

    [Theory]
    [InlineData("""{ "kind":"field","field":"name","rowBy":"owner" }""")]
    [InlineData("""{ "kind":"text","value":"hi","source":{"entity":"t"} }""")]
    [InlineData("""{ "kind":"view","view":"v","editable":true }""")]
    [InlineData("""{ "kind":"process","chartType":"pie" }""")]
    public void Foreign_properties_are_rejected(string json) => Assert.NotEmpty(BlockErrors(json));

    [Fact]
    public void An_unknown_kind_is_rejected()
        => Assert.NotEmpty(BlockErrors("""{ "kind":"totally_made_up" }"""));

    private static List<string> ErrorsFor(string defName, string json) =>
        Gate.StructuralErrors(JsonNode.Parse(json)!, DefSchema(defName));

    [Theory]
    [InlineData("filter", true, """{ "field":"status","operator":"eq","value":"open" }""")]
    [InlineData("filter", true, """{ "path":"room.tier","operator":"eq","value":"standard" }""")]
    [InlineData("filter", false, """{ "field":"status","path":"room.tier","operator":"eq","value":"x" }""")]
    [InlineData("filter", false, """{ "operator":"eq","value":"open" }""")]
    [InlineData("blockSource", true, """{ "entity":"task" }""")]
    [InlineData("blockSource", true, """{ "platform":"person" }""")]
    [InlineData("blockSource", false, """{ "entity":"task","platform":"person" }""")]
    [InlineData("blockSource", false, """{ "filters":[],"limit":10 }""")]
    [InlineData("condition", true, """{ "field":"status","operator":"eq","value":"open" }""")]
    [InlineData("condition", true, """{ "all":[{"field":"a","operator":"isEmpty"}] }""")]
    [InlineData("condition", false, """{ "all":[{"field":"a","operator":"isEmpty"}],"field":"b","operator":"eq","value":1 }""")]
    [InlineData("condition", false, """{ "all":[],"any":[] }""")]
    [InlineData("condition", false, """{ "value":"open" }""")]
    [InlineData("visibleWhen", true, """{ "value":"{{state.mode}}","eq":"week" }""")]
    [InlineData("visibleWhen", false, """{ "value":"{{state.mode}}" }""")]
    [InlineData("visibleWhen", false, """{ "value":"{{state.mode}}","eq":"week","neq":"day" }""")]
    public void Exactly_one_rules_are_enforced(string defName, bool valid, string json)
    {
        if (valid) Assert.Empty(ErrorsFor(defName, json));
        else Assert.NotEmpty(ErrorsFor(defName, json));
    }

    [Fact]
    public void A_dates_axis_must_be_bounded()
    {
        Assert.Empty(ErrorsFor("blockSource", """{ "dates":{"from":"{{today}}","count":7} }"""));
        Assert.Empty(ErrorsFor("blockSource", """{ "dates":{"from":"2026-01-01","to":"2026-01-31"} }"""));
        Assert.NotEmpty(ErrorsFor("blockSource", """{ "dates":{"from":"{{today}}"} }"""));
        Assert.NotEmpty(ErrorsFor("blockSource", """{ "dates":{"from":"{{today}}","to":"2026-01-31","count":7} }"""));
    }

    private static List<string> EffectErrors(string json) =>
        Gate.StructuralErrors(JsonNode.Parse(json)!, DefSchema("effect"));

    [Fact]
    public void A_clean_effect_of_each_type_still_passes()
    {
        Assert.Empty(EffectErrors("""{ "type":"updateRecord","set":{"status":"approved"} }"""));
        Assert.Empty(EffectErrors("""{ "type":"updateRecord","target":{"field":"room"},"set":{"busy":true} }"""));
        Assert.Empty(EffectErrors("""{ "type":"createRecord","entity":"task","set":{"name":"x"} }"""));
        Assert.Empty(EffectErrors("""{ "type":"notify","message":"Approved","link":"auto" }"""));
        Assert.Empty(EffectErrors("""{ "type":"email","to":"{{record.owner}}","subject":"Hi","body":"There" }"""));
        Assert.Empty(EffectErrors("""{ "type":"webhook","url":"https://example.com/hook" }"""));
    }

    [Theory]
    [InlineData("""{ "type":"notify","message":"m","url":"https://x.example","entity":"task" }""")]
    [InlineData("""{ "type":"updateRecord","set":{"a":1},"entity":"task" }""")]
    [InlineData("""{ "type":"webhook","url":"https://x.example","message":"m" }""")]
    [InlineData("""{ "type":"createRecord","entity":"task","set":{"a":1},"target":"self" }""")]
    [InlineData("""{ "type":"email","to":"a@b.c","subject":"s","body":"b","payload":{} }""")]
    public void An_effect_carrying_another_types_properties_is_rejected(string json)
        => Assert.NotEmpty(EffectErrors(json));

    [Theory]
    [InlineData("""{ "type":"updateRecord" }""")]
    [InlineData("""{ "type":"createRecord","set":{"a":1} }""")]
    [InlineData("""{ "type":"notify","title":"t" }""")]
    [InlineData("""{ "type":"email","to":"a@b.c","subject":"s" }""")]
    [InlineData("""{ "type":"webhook","method":"POST" }""")]
    [InlineData("""{ "set":{"a":1} }""")]
    [InlineData("""{ "type":"totally_made_up" }""")]
    public void Under_specified_effects_are_rejected(string json) => Assert.NotEmpty(EffectErrors(json));

    [Fact]
    public void A_webhook_url_must_be_https_at_authoring_time()
    {
        Assert.Empty(EffectErrors("""{ "type":"webhook","url":"https://example.com/hook" }"""));
        Assert.NotEmpty(EffectErrors("""{ "type":"webhook","url":"http://example.com/hook" }"""));
        Assert.NotEmpty(EffectErrors("""{ "type":"webhook","url":"example.com/hook" }"""));
    }

    [Fact]
    public void Shared_property_shapes_are_referenced_not_inlined()
    {
        var schema = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!;
        Assert.NotNull(schema["$defs"]!["visibleWhen"]);

        var inlined = 0;
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o["properties"]?["visibleWhen"] is JsonObject vw && vw["$ref"] is null) inlined++;
                    foreach (var kv in o) Walk(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var x in a) Walk(x);
                    break;
            }
        }
        Walk(schema["$defs"]);
        Assert.True(inlined == 0, $"{inlined} definition(s) still inline `visibleWhen` instead of $ref-ing it");

        Assert.Empty(BlockErrors("""{ "kind":"text","value":"x","visibleWhen":{"value":"{{state.mode}}","eq":"week"} }"""));
        Assert.NotEmpty(BlockErrors("""{ "kind":"text","value":"x","visibleWhen":{"eq":"week"} }"""));
        Assert.NotEmpty(BlockErrors("""{ "kind":"text","value":"x","visibleWhen":{"value":"v","banana":1} }"""));
    }

    private static IEnumerable<(JsonObject Block, string Where)> BlocksOf(JsonNode doc)
    {
        var hits = new List<(JsonObject, string)>();

        void Walk(JsonNode? blocks, string where)
        {
            if (blocks is not JsonArray arr) return;
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject b) continue;
                var here = $"{where}[{i}]";
                hits.Add((b, here));
                Walk(b["blocks"], here);
                if (b["tabs"] is JsonArray tabs)
                    for (var t = 0; t < tabs.Count; t++) Walk(tabs[t]?["blocks"], $"{here}.tabs[{t}]");
                if (b["columns"] is JsonArray cols)
                    for (var c = 0; c < cols.Count; c++) Walk(cols[c], $"{here}.columns[{c}]");
            }
        }

        foreach (var p in doc["pages"] as JsonArray ?? [])
            Walk(p?["blocks"], $"page '{p?["key"]}'");
        foreach (var e in doc["entities"] as JsonArray ?? [])
        {
            Walk(e?["detail"]?["blocks"], $"entity '{e?["key"]}'.detail");
        }
        return hits;
    }
}
