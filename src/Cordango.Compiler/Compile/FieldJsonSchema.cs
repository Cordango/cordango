// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compile;

/// <summary>
/// What a definition's field type looks like as JSON Schema.
///
/// <para><b>One map, because there are two products describing the same field.</b> The hosted
/// platform answers <c>openapi.json</c> from a manifest it holds at runtime; a generated standalone
/// application has its schemas emitted into its own source at build time. Both have to say the same
/// thing about a <c>money</c> field, and a second copy of this switch would drift the first time
/// somebody added a field type to one of them — silently, because neither product would fail.</para>
///
/// <para>It reads the manifest's field objects rather than any CLR type, which is not a limitation
/// to work around: the platform's records are property bags with no CLR type at all, and the
/// standalone target's entities are typed but its controllers take <c>JsonElement</c>, so reflection
/// has nothing useful to tell either of them.</para>
/// </summary>
public static class FieldJsonSchema
{
    /// <summary>
    /// One field.
    ///
    /// <para><paramref name="describe"/> adds the prose a person or a model reads — the field's
    /// label as <c>title</c>, its help text as <c>description</c>, and what a reference points at.
    /// Off by default so the platform's document is exactly what it always was; on for a generated
    /// application, where the same document is what an AI client reads instead of documentation.</para>
    /// </summary>
    public static JsonObject ForField(JsonObject field, bool describe = false)
    {
        ArgumentNullException.ThrowIfNull(field);

        var type = field["type"]?.GetValue<string>() ?? "text";

        var schema = type switch
        {
            "integer" => new JsonObject { ["type"] = "integer" },
            "decimal" or "money" => new JsonObject { ["type"] = "number" },
            "boolean" => new JsonObject { ["type"] = "boolean" },
            "multiselect" => new JsonObject { ["type"] = "array", ["items"] = Options(field) ?? new JsonObject { ["type"] = "string" } },
            "json" => new JsonObject { ["type"] = "object" },
            _ => new JsonObject { ["type"] = "string" },
        };

        switch (type)
        {
            case "date": schema["format"] = "date"; break;
            case "datetime": schema["format"] = "date-time"; break;
            case "email": schema["format"] = "email"; break;
            case "url": schema["format"] = "uri"; break;
        }

        // A closed set is worth stating twice: as an enum a client can validate against, and — under
        // `describe` — in the prose, because a model reading a tool schema acts on the sentence more
        // reliably than on a keyword some clients drop.
        if (type == "select" && Options(field) is { } choices)
            schema["enum"] = choices["enum"]!.DeepClone();

        if (!describe) return schema;

        if (field["label"]?.GetValue<string>() is { Length: > 0 } label) schema["title"] = label;
        if (Prose(field, type) is { Length: > 0 } prose) schema["description"] = prose;

        return schema;
    }

    /// <summary>
    /// An object schema over a chosen set of fields.
    ///
    /// <para>The caller chooses WHICH fields and which are required, because that differs by the job:
    /// reading a record shows everything including the columns the runtime fills in, creating one
    /// accepts only what a person may write, and a patch accepts the same set with nothing
    /// mandatory.</para>
    /// </summary>
    public static JsonObject ForObject(
        IEnumerable<JsonObject> fields, IReadOnlySet<string>? required = null, bool describe = false)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var properties = new JsonObject();
        var mandatory = new JsonArray();

        foreach (var field in fields)
        {
            if (field["key"]?.GetValue<string>() is not { } key) continue;
            properties[key] = ForField(field, describe);
            if (required?.Contains(key) == true) mandatory.Add(key);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (mandatory.Count > 0) schema["required"] = mandatory;

        // Refusing an unknown key is the difference between a client learning it misspelled a field
        // and a client watching its value vanish. Only under `describe`: the platform's document has
        // always been permissive and tightening it is a behaviour change for its existing consumers.
        if (describe) schema["additionalProperties"] = false;

        return schema;
    }

    /// <summary>The declared choices as an <c>enum</c> holder, or null when the field has none.
    /// Returned wrapped so a <c>multiselect</c> can use it as its <c>items</c> and a <c>select</c>
    /// can lift the array out.</summary>
    private static JsonObject? Options(JsonObject field)
    {
        if (field["options"] is not JsonArray options || options.Count == 0) return null;

        var values = options.OfType<JsonObject>()
            .Select(o => o["value"]?.GetValue<string>())
            .Where(v => v is not null)
            .Select(v => (JsonNode)JsonValue.Create(v!))
            .ToArray();

        return values.Length == 0
            ? null
            : new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values) };
    }

    /// <summary>The sentence under the field: its help text, then the facts a schema keyword cannot
    /// carry — what a reference points at, and the unit or currency a bare number is counted in.</summary>
    private static string Prose(JsonObject field, string type)
    {
        var parts = new List<string>(3);

        if (field["help"]?.GetValue<string>() is { Length: > 0 } help) parts.Add(help.TrimEnd('.') + ".");

        if (type == "reference" && field["targetEntity"]?.GetValue<string>() is { Length: > 0 } target)
            parts.Add($"The id of a {target} record.");

        if (field["currency"]?.GetValue<string>() is { Length: > 0 } currency) parts.Add($"In {currency}.");
        else if (field["unit"]?.GetValue<string>() is { Length: > 0 } unit) parts.Add($"In {unit}.");

        return string.Join(" ", parts);
    }
}
