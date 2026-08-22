// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class SchemaCompositionTests
{
    private static readonly JsonObject Schema =
        JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!.AsObject();

    private static JsonObject Defs => Schema["$defs"]!.AsObject();

    [Fact]
    public void Embedded_schema_parses_under_JsonSchemaNet()
    {
        Assert.NotNull(Schemas.AppDefinitionSchema);
        Assert.Equal(81, Defs.Count);
    }

    [Fact]
    public void Block_dispatch_covers_every_block_variant()
    {
        var variantKinds = Defs
            .Where(d => d.Key.StartsWith("block_"))
            .Select(d => d.Key["block_".Length..])
            .OrderBy(k => k)
            .ToArray();
        Assert.Equal(41, variantKinds.Length);

        var dispatched = Defs["block"]!["allOf"]!.AsArray()
            .Select(branch => branch?["if"]?["properties"]?["kind"]?["const"]?.GetValue<string>())
            .Where(k => k is not null)
            .OrderBy(k => k)
            .ToArray();
        Assert.Equal(variantKinds, dispatched);

        var enumKinds = Defs["block"]!["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(k => k!.GetValue<string>()).OrderBy(k => k).ToArray();
        Assert.Equal(variantKinds, enumKinds);
    }

    [Fact]
    public void Effect_dispatch_covers_every_effect_type()
    {
        var variantTypes = Defs.Where(d => d.Key.StartsWith("effect_"))
            .Select(d => d.Key["effect_".Length..]).OrderBy(t => t).ToArray();
        Assert.Equal(
            new[] { "createForEach", "createRecord", "email", "enrich", "notify", "updateRecord", "webhook" },
            variantTypes);

        var dispatched = Defs["effect"]!["allOf"]!.AsArray()
            .Select(branch => branch?["if"]?["properties"]?["type"]?["const"]?.GetValue<string>())
            .Where(t => t is not null)
            .OrderBy(t => t)
            .ToArray();
        Assert.Equal(variantTypes, dispatched);

        var enumTypes = Defs["effect"]!["properties"]!["type"]!["enum"]!.AsArray()
            .Select(t => t!.GetValue<string>()).OrderBy(t => t).ToArray();
        Assert.Equal(variantTypes, enumTypes);
    }

    [Fact]
    public void UnevaluatedProperties_never_appears()
    {
        var offenders = new List<string>();
        Walk(Schema, "$", offenders);
        Assert.Empty(offenders);

        static void Walk(JsonNode? node, string path, List<string> offenders)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "unevaluatedProperties") offenders.Add(path);
                        Walk(v, $"{path}.{k}", offenders);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", offenders);
                    break;
            }
        }
    }

    [Fact]
    public void Every_local_ref_resolves_and_no_ref_is_external()
    {
        var missing = new List<string>();
        Walk(Schema, "$", missing);
        Assert.Empty(missing);

        void Walk(JsonNode? node, string path, List<string> missing)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k == "$ref" && v is JsonValue val
                            && val.TryGetValue<string>(out var r))
                        {
                            const string prefix = "#/$defs/";
                            if (!r.StartsWith(prefix) || !Defs.ContainsKey(r[prefix.Length..]))
                                missing.Add($"{path}: {r}");
                        }
                        else Walk(v, $"{path}.{k}", missing);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", missing);
                    break;
            }
        }
    }
}
