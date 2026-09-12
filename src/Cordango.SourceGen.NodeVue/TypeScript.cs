// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.NodeVue;

/// <summary>
/// How a definition's words become TypeScript.
///
/// <para><b>A field keeps its key, and that is the whole naming policy.</b> The .NET target has to
/// translate — <c>spent_on</c> becomes <c>SpentOn</c>, because a C# property called
/// <c>spent_on</c> would be unidiomatic and the JSON name has to be reattached with an attribute.
/// TypeScript has no such problem: an object's key can be exactly the field's key, so the wire
/// shape, the database column and the property a developer types are one string. That removes a
/// whole class of bug the .NET target has to test for — a mapping that got it wrong, in one
/// direction only, on one of four audit columns.</para>
///
/// <para>Only TYPE names are transformed, because a type is a name this generator invents rather
/// than one the definition gave it.</para>
/// </summary>
public static class TypeScript
{
    /// <summary>The interface name for an entity: <c>expense_claim</c> becomes
    /// <c>ExpenseClaim</c>.</summary>
    public static string TypeName(string key) => Naming.Pascal(key);

    /// <summary>A camelCase identifier, for a function or a constant this generator names.</summary>
    public static string Identifier(string key)
    {
        var pascal = Naming.Pascal(key);
        if (pascal.Length == 0) return "value";
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>
    /// A string as TypeScript source.
    ///
    /// <para>Double-quoted, with the four escapes that matter and a numeric escape for anything
    /// below the space. The numeric form is what keeps a definition containing a control character —
    /// pasted out of a spreadsheet, most often — from producing a source file that will not
    /// parse.</para>
    /// </summary>
    public static string Literal(string? value)
    {
        if (value is null) return "null";

        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (var character in value)
            builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                // Line and paragraph separators are legal in a TS string but break older tooling
                // that reads the file as lines. Escaped for the same reason as the controls.
                (char)0x2028 => "\\u2028",
                (char)0x2029 => "\\u2029",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });

        return builder.Append('"').ToString();
    }

    /// <summary>A key as it appears in an object literal: bare when it is a plain identifier, quoted
    /// otherwise. Quoting everything would be safe and would read like generated code.</summary>
    public static string Key(string key) =>
        IsPlainIdentifier(key) ? key : Literal(key);

    private static bool IsPlainIdentifier(string key)
    {
        if (key.Length == 0) return false;
        if (!char.IsLetter(key[0]) && key[0] != '_' && key[0] != '$') return false;

        foreach (var character in key)
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '$')
                return false;

        return true;
    }

    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// A JSON node as a TypeScript expression.
    ///
    /// <para>Used for the parts of a definition that travel as DATA rather than as code: a
    /// condition, a filter, a block's options. Written as an object literal rather than as a parsed
    /// JSON string so that it is readable, diffable and type-checked where a type applies.</para>
    /// </summary>
    public static string Value(JsonNode? node, int indent = 0)
    {
        switch (node)
        {
            case null:
                return "null";

            case JsonValue value:
                if (value.TryGetValue<bool>(out var flag)) return Bool(flag);
                if (value.TryGetValue<string>(out var text)) return Literal(text);
                // Round-tripped through the raw JSON text, so a decimal with more precision than a
                // double holds is not quietly rounded on its way into the source.
                return value.ToJsonString();

            case JsonArray array:
            {
                if (array.Count == 0) return "[]";

                var pad = new string(' ', indent + 2);
                var items = array.Select(item => pad + Value(item, indent + 2));
                return "[\n" + string.Join(",\n", items) + "\n" + new string(' ', indent) + "]";
            }

            case JsonObject obj:
            {
                if (obj.Count == 0) return "{}";

                var pad = new string(' ', indent + 2);
                var entries = obj.Select(pair =>
                    $"{pad}{Key(pair.Key)}: {Value(pair.Value, indent + 2)}");
                return "{\n" + string.Join(",\n", entries) + "\n" + new string(' ', indent) + "}";
            }

            default:
                return "null";
        }
    }

    /// <summary>
    /// The TypeScript type of one field's value, and whether it can be absent.
    ///
    /// <para>Optional means <c>| null</c>, and that is not a style choice: a definition's optional
    /// field has no value until somebody enters one, and representing "not entered" as zero or as an
    /// empty string would make a sum wrong and a filter lie.</para>
    ///
    /// <para>Money and decimals are <c>Dec</c>, never <c>number</c>. A JavaScript number cannot hold
    /// 0.1 + 0.2 correctly, and an invoice total is exactly the value nobody may round for you.
    /// <c>Dec</c> is the runtime's own decimal and is what the query layer, the aggregate and the
    /// database codec already speak.</para>
    /// </summary>
    public static string FieldType(FieldModel field)
    {
        var bare = field.Type switch
        {
            "integer" => "number",
            "decimal" or "money" => "Dec",
            "boolean" => "boolean",
            "date" => "PlainDate",
            "datetime" => "Instant",
            "multiselect" => "string[]",
            "json" => "Record<string, unknown>",
            // text, longtext, email, url, phone, select, reference, attachment — all text in the
            // data plane. A reference holds the target record's id, which is a string.
            _ => "string",
        };

        // A list is never null: an empty selection is an empty list, not a missing one, and making
        // every reader check for both would put a null test at every use.
        if (bare == "string[]") return bare;

        return field.Required ? bare : bare + " | null";
    }

    /// <summary>The field type the runtime's descriptor knows, which is a smaller set than the
    /// language's: everything that is text in the data plane is <c>text</c> here.</summary>
    public static string DescriptorType(string cordType) => cordType switch
    {
        "integer" => "integer",
        "decimal" => "decimal",
        "money" => "money",
        "boolean" => "boolean",
        "date" => "date",
        "datetime" => "datetime",
        "reference" => "reference",
        "multiselect" => "multiselect",
        "json" => "json",
        _ => "text",
    };

    /// <summary>A prose block as a TypeScript doc comment, wrapped at a readable width.</summary>
    public static string Doc(string text, int indent = 0)
    {
        var pad = new string(' ', indent);
        var lines = Wrap(text, 96 - indent);

        if (lines.Count == 1) return $"{pad}/** {lines[0]} */";

        var builder = new StringBuilder();
        builder.Append(pad).Append("/**\n");
        foreach (var line in lines)
            builder.Append(pad).Append(line.Length == 0 ? " *" : " * " + line).Append('\n');
        builder.Append(pad).Append(" */");
        return builder.ToString();
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }

            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }

            if (current.Length > 0) lines.Add(current.ToString());
        }

        return lines.Count == 0 ? [""] : lines;
    }
}
