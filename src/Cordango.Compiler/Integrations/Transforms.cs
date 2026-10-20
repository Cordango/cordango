// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Integrations;

/// <summary>
/// The closed set of steps a mapping may apply to a provider value, and the type check that keeps a
/// chain honest: a <c>divide</c> after a <c>concat</c>, or an <c>enum</c> producing a value the target
/// select does not have, is refused here rather than discovered as a blank column.
///
/// <para><b>No code, ever.</b> A mapping is data, so two implementations (the platform's evaluator,
/// later emitted standalone code) can be held to the same public fixtures in
/// <c>tests/fixtures/transforms</c>. Computing a value is execution and is not in this assembly.</para>
/// </summary>
public static class Transforms
{
    /// <summary>The kind of value flowing through a chain. A raw provider value is <see cref="Any"/>:
    /// nothing is known about it until a step says so.</summary>
    public enum ValueKind { Any, Text, Number, Boolean, Date, List }

    public static readonly IReadOnlyList<string> Names =
        ["default", "coalesce", "enum", "lookup", "concat", "split", "date", "multiply", "divide", "template"];

    private static readonly Dictionary<string, string> Companion = new(StringComparer.Ordinal)
    {
        ["otherwise"] = "enum|lookup",
        ["separator"] = "concat",
        ["index"] = "split",
    };

    /// <summary>The steps of a <c>transform</c> value: one object, or a list of them, in order.</summary>
    public static IReadOnlyList<JsonObject> Steps(JsonNode? transform) => transform switch
    {
        JsonObject one => [one],
        JsonArray many => [.. many.OfType<JsonObject>()],
        _ => [],
    };

    /// <summary>True when the chain rewrites values (enum, lookup). A filter on such a field has to
    /// translate its value back explicitly, or it is sent in a vocabulary the provider does not use
    /// and matches nothing.</summary>
    public static bool Translates(JsonNode? transform) =>
        Steps(transform).Any(s => s.ContainsKey("enum") || s.ContainsKey("lookup"));

    /// <summary>The transform key of one step, or null when it has none or several.</summary>
    public static string? NameOf(JsonObject step)
    {
        var names = step.Select(kv => kv.Key).Where(Names.Contains).ToList();
        return names.Count == 1 ? names[0] : null;
    }

    /// <summary>Type-checks a chain ending in <paramref name="target"/>.</summary>
    public static void Check(JsonNode? transform, ContractField target, string where, List<string> errors)
    {
        var kind = ValueKind.Any;
        foreach (var step in Steps(transform))
        {
            var name = NameOf(step);
            if (name is null)
            {
                errors.Add($"SEMANTIC: {where}: a transform step names exactly one of {string.Join(", ", Names)}");
                return;
            }
            foreach (var key in step.Select(kv => kv.Key).Where(k => k != name))
                if (!Companion.TryGetValue(key, out var with) || !with.Split('|').Contains(name))
                    errors.Add($"SEMANTIC: {where}: '{key}' does not belong beside '{name}'");

            var next = Step(name, step, kind, target, where, errors);
            if (next is null) return;
            kind = next.Value;
        }

        if (!Fits(kind, target.Type))
            errors.Add($"SEMANTIC: {where}: the mapping produces {Describe(kind)}, and '{target.Key}' is a {target.Type} field");
    }

    private static ValueKind? Step(string name, JsonObject step, ValueKind input, ContractField target,
        string where, List<string> errors)
    {
        var at = $"{where}: {name}";
        switch (name)
        {
            case "default":
                return step["default"] switch
                {
                    JsonValue v when v.TryGetValue<string>(out _) => input == ValueKind.Any ? ValueKind.Any : input,
                    JsonValue v when v.TryGetValue<double>(out _) => input == ValueKind.Any ? ValueKind.Any : input,
                    JsonValue v when v.TryGetValue<bool>(out _) => input == ValueKind.Any ? ValueKind.Any : input,
                    _ => Fail(at, "takes a single text, number or true/false value", errors),
                };

            case "coalesce":
                foreach (var p in (step["coalesce"] as JsonArray ?? []).Select(n => n?.GetValue<string>()))
                    if (ApiPath.TryParse(p, out var perr) is null) errors.Add($"SEMANTIC: {at}: {perr}");
                return input;

            case "enum":
            {
                var map = step["enum"] as JsonObject ?? [];
                var produced = map.Select(kv => kv.Value?.GetValue<string>()).OfType<string>().ToList();
                if (step["otherwise"] is JsonValue ov && ov.TryGetValue<string>(out var otherwise)) produced.Add(otherwise);
                else if (step["otherwise"] is not null) return Fail(at, "'otherwise' is one of the target's option values", errors);
                if (target.Type is "select" or "multiselect")
                    foreach (var value in produced.Distinct(StringComparer.Ordinal).Where(v => !target.Options.Contains(v)))
                        errors.Add($"SEMANTIC: {at}: produces '{value}', which '{target.Key}' does not have "
                                 + $"(its options: {string.Join(", ", target.Options)})");
                return ValueKind.Text;
            }

            case "lookup":
            {
                var values = (step["lookup"] as JsonObject ?? []).Select(kv => kv.Value).ToList();
                if (step["otherwise"] is { } other) values.Add(other);
                var kinds = values.Select(Literal).Distinct().ToList();
                return kinds.Count == 1 ? kinds[0] : ValueKind.Any;
            }

            case "concat":
                foreach (var part in (step["concat"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
                    if (part.StartsWith('$') && ApiPath.TryParse(part, out var cerr) is null)
                        errors.Add($"SEMANTIC: {at}: {cerr}");
                return ValueKind.Text;

            case "split":
                if (input is not (ValueKind.Any or ValueKind.Text))
                    return Fail(at, $"splits text, and receives {Describe(input)}", errors);
                return step["index"] is null ? ValueKind.List : ValueKind.Text;

            case "date":
                if (input is not (ValueKind.Any or ValueKind.Text or ValueKind.Number))
                    return Fail(at, $"reads text or a number, and receives {Describe(input)}", errors);
                return ValueKind.Date;

            case "multiply" or "divide":
            {
                if (input is not (ValueKind.Any or ValueKind.Number))
                    return Fail(at, $"needs a number, and receives {Describe(input)}", errors);
                if (name == "divide" && step["divide"] is JsonValue d && d.TryGetValue<double>(out var by) && by == 0)
                    return Fail(at, "cannot divide by zero", errors);
                return ValueKind.Number;
            }

            case "template":
            {
                var text = step["template"]?.GetValue<string>() ?? "";
                var names = RequestTemplate.Variables(text, out var terr);
                if (terr is not null) errors.Add($"SEMANTIC: {at}: {terr}");
                foreach (var n in names)
                    if (ApiPath.TryParse(n, out var perr) is null)
                        errors.Add($"SEMANTIC: {at}: placeholders read the provider item by path ({{{{$.name}}}}) — {perr}");
                return ValueKind.Text;
            }
        }
        return null;
    }

    private static ValueKind Literal(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out _) => ValueKind.Text,
        JsonValue v when v.TryGetValue<bool>(out _) => ValueKind.Boolean,
        JsonValue v when v.TryGetValue<double>(out _) => ValueKind.Number,
        _ => ValueKind.Any,
    };

    private static ValueKind? Fail(string at, string why, List<string> errors)
    {
        errors.Add($"SEMANTIC: {at} {why}");
        return null;
    }

    /// <summary>Whether a value of <paramref name="kind"/> can land in a field of <paramref name="type"/>.
    /// <see cref="ValueKind.Any"/> always can: the provider decides, and the platform coerces or reports.</summary>
    public static bool Fits(ValueKind kind, string type) => kind == ValueKind.Any || type switch
    {
        "text" or "longtext" or "email" or "url" or "phone" or "select" => kind == ValueKind.Text,
        "integer" or "decimal" or "money" => kind == ValueKind.Number,
        "boolean" => kind == ValueKind.Boolean,
        "date" or "datetime" => kind == ValueKind.Date,
        "multiselect" => kind == ValueKind.List,
        "json" => true,
        _ => false,
    };

    private static string Describe(ValueKind kind) => kind switch
    {
        ValueKind.Text => "text",
        ValueKind.Number => "a number",
        ValueKind.Boolean => "true/false",
        ValueKind.Date => "a date",
        ValueKind.List => "a list",
        _ => "any value",
    };
}
