// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compile;

/// <summary>
/// Emits an OpenAPI 3.0 document for an app's REST data facade straight from its compiled manifest —
/// because the entities are property bags with no CLR types, the schema can't come from reflection.
/// One collection + item path per entity, and a component schema per entity from its field types.
/// </summary>
public static class OpenApiFromManifest
{
    public static JsonObject Build(JsonObject manifest, string basePath)
    {
        var name = manifest["name"]?.GetValue<string>() ?? "App";
        var version = manifest["build"]?["source"]?["version"]?.GetValue<string>()
                      ?? manifest["version"]?.GetValue<string>() ?? "1.0.0";

        var paths = new JsonObject();
        var schemas = new JsonObject();

        foreach (var e in (manifest["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (e["key"]?.GetValue<string>() is not { } key) continue;
            var label = e["label"]?.GetValue<string>() ?? key;
            schemas[key] = EntitySchema(e);

            paths[$"{basePath}/{key}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["summary"] = $"List {label}", ["responses"] = Resp(ListSchema(key)) },
                ["post"] = new JsonObject { ["summary"] = $"Create {label}", ["requestBody"] = Body(key), ["responses"] = Resp(Ref(key)) },
            };
            paths[$"{basePath}/{key}/{{recordId}}"] = new JsonObject
            {
                ["get"] = new JsonObject { ["summary"] = $"Get {label}", ["parameters"] = IdParam(), ["responses"] = Resp(Ref(key)) },
                ["put"] = new JsonObject { ["summary"] = $"Update {label}", ["parameters"] = IdParam(), ["requestBody"] = Body(key), ["responses"] = Resp(Ref(key)) },
                ["delete"] = new JsonObject { ["summary"] = $"Delete {label}", ["parameters"] = IdParam(), ["responses"] = Resp(null) },
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = $"{name} API", ["version"] = version },
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas },
        };
    }

    /// <summary>The field-type map lives in <see cref="FieldJsonSchema"/> — shared with the
    /// standalone target, which emits the same schemas into a generated application's source rather
    /// than answering them from a manifest.</summary>
    private static JsonObject EntitySchema(JsonObject entity)
    {
        var fields = (entity["fields"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        var required = fields
            .Where(f => f["required"]?.GetValue<bool>() == true && f["system"]?.GetValue<bool>() != true)
            .Select(f => f["key"]?.GetValue<string>())
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

        return FieldJsonSchema.ForObject(fields, required);
    }

    private static JsonObject Ref(string key) => new() { ["$ref"] = $"#/components/schemas/{key}" };

    private static JsonObject ListSchema(string key) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = Ref(key) },
            ["total"] = new JsonObject { ["type"] = "integer" },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["pageSize"] = new JsonObject { ["type"] = "integer" },
        },
    };

    private static JsonObject Body(string key) => new()
    {
        ["required"] = true,
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(key) } },
    };

    private static JsonObject Resp(JsonNode? schema)
    {
        var ok = new JsonObject { ["description"] = "OK" };
        if (schema is not null)
            ok["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } };
        return new JsonObject { ["200"] = ok };
    }

    private static JsonArray IdParam() => new(new JsonObject
    {
        ["name"] = "recordId",
        ["in"] = "path",
        ["required"] = true,
        ["schema"] = new JsonObject { ["type"] = "string" },
    });
}
