// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Integrations;

public sealed record ContractOutcome(IReadOnlyList<string> Errors, IntegrationContract? Contract)
{
    public bool Ok => Errors.Count == 0 && Contract is not null;
}

/// <summary>
/// Turns an authored contract into the canonical document apps are checked against.
///
/// <para><b>Deterministic.</b> Entities are ordered by key, a field's declared order is kept (it is
/// what a table shows first), sets are sorted, defaults are written out. The same source always
/// produces the same bytes and the same hash.</para>
/// </summary>
public static class ContractCompiler
{
    public const string Kind = "IntegrationContract";

    private static readonly IReadOnlySet<string> RangeTypes =
        new HashSet<string>(StringComparer.Ordinal) { "integer", "decimal", "money", "date", "datetime" };
    private static readonly IReadOnlySet<string> NumericTypes =
        new HashSet<string>(StringComparer.Ordinal) { "integer", "decimal", "money" };
    private static readonly IReadOnlySet<string> TextTypes =
        new HashSet<string>(StringComparer.Ordinal) { "text", "longtext", "email", "url", "phone", "multiselect" };
    private static readonly IReadOnlySet<string> Writes =
        new HashSet<string>(StringComparer.Ordinal) { "create", "update", "delete" };

    public static ContractOutcome Compile(JsonNode? source)
    {
        source = Normalized(source);
        var errors = Gate.StructuralErrors(source, IntegrationSchemas.ContractNode);
        if (errors.Count > 0) return new ContractOutcome(errors, null);
        var doc = (JsonObject)source!;

        var name = Str(doc, "name")!;
        var major = doc["version"]!.GetValue<int>();
        var label = Str(doc, "label") ?? name;

        var entities = new List<ContractEntity>();
        var compiledEntities = new JsonArray();
        foreach (var (key, node) in ((JsonObject)doc["entities"]!).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var where = $"{name} entity '{key}'";
            var e = (JsonObject)node!;
            var fields = new List<ContractField>();
            var compiledFields = new JsonArray();
            foreach (var (fkey, fnode) in (JsonObject)e["fields"]!)
            {
                var f = (JsonObject)fnode!;
                var type = Str(f, "type")!;
                var fwhere = $"{where} field '{fkey}'";
                if (fkey.Contains("__", StringComparison.Ordinal))
                    errors.Add($"SEMANTIC: {fwhere}: '__' is reserved for provider-specific fields (hubspot__lifecycle_stage) and cannot appear in a contract field");
                if (Gate.BaseFields.Contains(fkey) && fkey != "id" && fkey != "created_at" && fkey != "updated_at")
                    errors.Add($"SEMANTIC: {fwhere}: '{fkey}' is a field the platform reserves on every record");
                var options = OptionValues(f["options"]);
                if (type is "select" or "multiselect" && options.Count == 0)
                    errors.Add($"SEMANTIC: {fwhere}: a {type} field needs 'options'");
                if (type is not ("select" or "multiselect") && f["options"] is not null)
                    errors.Add($"SEMANTIC: {fwhere}: 'options' belong to a select or multiselect field, not a {type}");
                if (options.Count != options.Distinct(StringComparer.Ordinal).Count())
                    errors.Add($"SEMANTIC: {fwhere}: an option value is listed twice");

                var flabel = Str(f, "label") ?? Humanize(fkey);
                var required = f["required"]?.GetValue<bool>() == true;
                var readOnly = f["readonly"]?.GetValue<bool>() == true;
                fields.Add(new ContractField(fkey, flabel, type, required, readOnly, options));

                var cf = new JsonObject { ["key"] = fkey, ["label"] = flabel, ["type"] = type };
                if (required) cf["required"] = true;
                if (readOnly) cf["readonly"] = true;
                if (options.Count > 0)
                    cf["options"] = new JsonArray([.. OptionObjects(f["options"])]);
                compiledFields.Add(cf);
            }

            var fieldByKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
            if (!fieldByKey.TryGetValue("id", out var idField))
                errors.Add($"SEMANTIC: {where}: needs an 'id' field — the upstream's own identity, which is what a row is opened and linked by");
            else if (idField.Type is not ("text" or "integer"))
                errors.Add($"SEMANTIC: {where}: 'id' is a {idField.Type}; an identity is text or an integer");
            var display = Str(e, "displayField")!;
            if (!fieldByKey.ContainsKey(display))
                errors.Add($"SEMANTIC: {where}: displayField '{display}' is not one of its fields");

            var capabilities = (e["capabilities"] as JsonArray ?? []).Select(c => c!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var w in capabilities.Where(Writes.Contains).Order(StringComparer.Ordinal))
                errors.Add($"SEMANTIC: {where}: capability '{w}' — integrations are read-only for now; "
                         + "writes arrive with explicit toProvider mappings");
            if (!capabilities.Contains("list") && !capabilities.Contains("get"))
                errors.Add($"SEMANTIC: {where}: needs 'list' or 'get' — an entity nothing can read has nothing to offer an app");

            var query = e["query"] as JsonObject;
            var filters = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (var (ffield, opsNode) in query?["filter"] as JsonObject ?? [])
            {
                var ops = (opsNode as JsonArray ?? []).Select(o => o!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                if (!fieldByKey.TryGetValue(ffield, out var target))
                { errors.Add($"SEMANTIC: {where}: query filter '{ffield}' is not one of its fields"); continue; }
                foreach (var op in ops.Order(StringComparer.Ordinal))
                    if (OperatorProblem(op, target.Type) is { } why)
                        errors.Add($"SEMANTIC: {where}: filter '{ffield} {op}' — {why}");
                filters[ffield] = ops;
            }
            var sorts = (query?["sort"] as JsonArray ?? []).Select(s => s!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var s in sorts.Order(StringComparer.Ordinal))
            {
                if (!fieldByKey.TryGetValue(s, out var target))
                    errors.Add($"SEMANTIC: {where}: query sort '{s}' is not one of its fields");
                else if (target.Type is "json" or "multiselect" or "longtext")
                    errors.Add($"SEMANTIC: {where}: query sort '{s}' — a {target.Type} field has no order");
            }

            var aggregates = Aggregates(query?["aggregate"] as JsonObject, fieldByKey, where, errors);
            if ((filters.Count > 0 || sorts.Count > 0 || aggregates.Any) && !capabilities.Contains("list"))
                errors.Add($"SEMANTIC: {where}: a 'query' needs the 'list' capability — filters, sorts and figures are about lists");

            var plural = Str(e, "labelPlural") ?? Humanize(key) + "s";
            var elabel = Str(e, "label") ?? Humanize(key);
            entities.Add(new ContractEntity(key, elabel, plural, display, fields, capabilities,
                filters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), sorts, aggregates));

            var ce = new JsonObject
            {
                ["key"] = key,
                ["label"] = elabel,
                ["labelPlural"] = plural,
                ["displayField"] = display,
                ["fields"] = compiledFields,
                ["capabilities"] = new JsonArray([.. capabilities.Order(StringComparer.Ordinal).Select(c => (JsonNode)c!)]),
            };
            var cq = new JsonObject();
            if (filters.Count > 0)
                cq["filter"] = new JsonObject(filters.Select(kv => KeyValuePair.Create(kv.Key,
                    (JsonNode?)new JsonArray([.. kv.Value.Order(StringComparer.Ordinal).Select(o => (JsonNode)o!)]))));
            if (sorts.Count > 0)
                cq["sort"] = new JsonArray([.. sorts.Order(StringComparer.Ordinal).Select(s => (JsonNode)s!)]);
            if (AggregateJson(aggregates) is { } aj) cq["aggregate"] = aj;
            if (cq.Count > 0) ce["query"] = cq;
            compiledEntities.Add(ce);
        }

        if (errors.Count > 0) return new ContractOutcome(errors, null);

        var compiled = new JsonObject
        {
            ["kind"] = "integration-contract",
            ["contractVersion"] = "1.0",
            ["name"] = name,
            ["version"] = major,
            ["ref"] = $"{name}@{major}",
            ["label"] = label,
        };
        if (Str(doc, "description") is { } description) compiled["description"] = description;
        compiled["entities"] = compiledEntities;
        var hash = DefinitionHash.Of(compiled);
        compiled["hash"] = hash;

        return new ContractOutcome([], new IntegrationContract(name, major, label, entities, compiled, hash));
    }

    private static string? OperatorProblem(string op, string type) => op switch
    {
        _ when type == "json" => "a json field cannot be filtered",
        "gt" or "gte" or "lt" or "lte" when !RangeTypes.Contains(type) => $"'{op}' compares numbers and dates, and this field is {type}",
        "contains" when !TextTypes.Contains(type) => $"'contains' searches text, and this field is {type}",
        _ => null,
    };

    private static ContractAggregates Aggregates(JsonObject? agg, Dictionary<string, ContractField> fields,
        string where, List<string> errors)
    {
        if (agg is null) return ContractAggregates.None;
        IReadOnlySet<string> Fields(string op, IReadOnlySet<string> allowed, string what)
        {
            var set = (agg[op] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var f in set.Order(StringComparer.Ordinal))
            {
                if (!fields.TryGetValue(f, out var target))
                    errors.Add($"SEMANTIC: {where}: aggregate {op} names '{f}', which is not one of its fields");
                else if (!allowed.Contains(target.Type))
                    errors.Add($"SEMANTIC: {where}: aggregate {op} over '{f}' needs {what}, and it is {target.Type}");
            }
            return set;
        }
        var count = agg["count"]?.GetValue<bool>() == true;
        return new ContractAggregates(count,
            Fields("sum", NumericTypes, "a number"),
            Fields("avg", NumericTypes, "a number"),
            Fields("min", RangeTypes, "a number or a date"),
            Fields("max", RangeTypes, "a number or a date"));
    }

    private static JsonObject? AggregateJson(ContractAggregates a)
    {
        if (!a.Any) return null;
        var o = new JsonObject();
        if (a.Count) o["count"] = true;
        foreach (var (op, set) in new[] { ("sum", a.Sum), ("avg", a.Avg), ("min", a.Min), ("max", a.Max) })
            if (set.Count > 0) o[op] = new JsonArray([.. set.Order(StringComparer.Ordinal).Select(s => (JsonNode)s!)]);
        return o.Count > 0 ? o : null;
    }

    private static List<string> OptionValues(JsonNode? options) =>
        [.. (options as JsonArray ?? []).Select(o => o switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonObject obj => Str(obj, "value"),
            _ => null,
        }).OfType<string>()];

    private static IEnumerable<JsonNode> OptionObjects(JsonNode? options)
    {
        foreach (var o in options as JsonArray ?? [])
        {
            var value = o is JsonValue v ? v.GetValue<string>() : Str((JsonObject)o!, "value")!;
            var label = o is JsonObject obj ? Str(obj, "label") ?? Humanize(value) : Humanize(value);
            yield return new JsonObject { ["value"] = value, ["label"] = label };
        }
    }

    /// <summary>
    /// The document re-read from its own JSON text, so every number answers to every numeric type.
    ///
    /// <para>A document built in code or read from YAML holds typed values — the YAML reader produces
    /// a <c>long</c> for every integer — and a typed value refuses to be read as an <c>int</c> or a
    /// <c>double</c>. Without this, a version written in YAML throws, and <c>divide: 0</c> slips past
    /// the zero check because it never reads as a number at all.</para>
    /// </summary>
    internal static JsonNode? Normalized(JsonNode? source) =>
        source is null ? null : JsonNode.Parse(source.ToJsonString());

    internal static string Humanize(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    internal static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
