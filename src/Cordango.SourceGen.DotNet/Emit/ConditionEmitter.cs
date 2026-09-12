// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// A condition from the definition, as a C# expression a person can read.
///
/// <para>The definition writes JSON:</para>
/// <code>
/// { "all": [ { "field": "runway_months", "operator": "lt", "value": 6 },
///            { "field": "runway_months", "operator": "isNotEmpty" } ] }
/// </code>
/// <para>and this writes:</para>
/// <code>
/// Condition.Every(
///     Condition.Leaf("runway_months", "lt", "6"),
///     Condition.Leaf("runway_months", "isNotEmpty"))
/// </code>
///
/// <para><b>Data rather than code, unlike a computed field.</b> A guard is a small tree that the
/// runtime walks; compiling it into an <c>if</c> would gain nothing and would mean the same language
/// had two implementations in the same application — one for guards written as C#, one for filters
/// still evaluated as data. One evaluator, one language.</para>
///
/// <para><b>Every value becomes a string.</b> The definition's <c>6</c> and <c>"closed"</c> both
/// come out quoted, because <see cref="Cordango.Standalone.Conditions.ConditionEvaluator"/> compares
/// numerically whenever both sides read as numbers. Carrying JSON literals into generated code to
/// preserve a distinction the comparison does not make would cost readability for nothing.</para>
/// </summary>
public static class ConditionEmitter
{
    /// <summary>
    /// The C# for this condition, and whether it could be written at all.
    ///
    /// <para><b>A condition this cannot render must not come out as <c>null</c>.</b> <c>null</c> is
    /// the literal for "there is no condition here", and a command whose guard silently became "no
    /// guard" is a command that runs when the definition said it must not. That is the one failure
    /// mode of a generator that a person cannot see by reading the application it produced, so the
    /// caller is told and the build refuses rather than shipping the permissive version.</para>
    ///
    /// <para>Returns true when there is nothing to render either — no condition is not a failure to
    /// render one, and <paramref name="code"/> is then the <c>null</c> literal.</para>
    /// </summary>
    public static bool TryEmit(JsonNode? condition, out string code)
    {
        var rendered = Render(condition);
        code = rendered ?? "null";

        // Nothing to render is not a failure to render. Only a condition that IS there and did not
        // come out is.
        return rendered is not null || condition is not JsonObject { Count: > 0 };
    }

    /// <summary>The C# for this condition, or the <c>null</c> literal. Prefer
    /// <see cref="TryEmit"/> anywhere a dropped condition would change what the application does —
    /// this cannot tell you the difference between "no guard" and "a guard I could not write".</summary>
    public static string Emit(JsonNode? condition) => Render(condition) ?? "null";

    /// <summary>The C# for this condition, or <c>null</c> meaning it could not be written. The
    /// distinction propagates: a composite one of whose children could not be written could not be
    /// written either, because emitting the rest would produce a WEAKER guard than the definition
    /// states and nothing downstream would ever know.</summary>
    private static string? Render(JsonNode? condition)
    {
        if (condition is not JsonObject leaf) return null;

        if (leaf["all"] is JsonArray all)
            return Composite("Every", all);

        if (leaf["any"] is JsonArray any)
            return Composite("Some", any);

        if (leaf["not"] is JsonObject not)
            return Render(not) is { } inner ? $"Condition.Never({inner})" : null;

        if (AppModel.Str(leaf["operator"]) is not { } op) return null;

        var target = AppModel.Str(leaf["field"]) is { Length: > 0 } field
            ? ("Leaf", field)
            : AppModel.Str(leaf["path"]) is { Length: > 0 } path ? ("Hop", path) : default;

        if (target.Item1 is null) return null;

        var value = leaf["value"];

        // The list operators. `Values` rather than `Value`, and a distinct overload, so that reading
        // the generated line tells you which kind of comparison happens without knowing the operator
        // table by heart.
        if (value is JsonArray items)
        {
            var elements = string.Join(", ", items.Select(item => Naming.Literal(Scalar(item))));
            var ends = AppModel.Str(leaf["endField"]) is { Length: > 0 } endField
                ? $", EndField: {Naming.Literal(endField)}"
                : "";

            // `overlaps` needs the record's other endpoint, which no factory overload takes — the
            // object initialiser says so plainly rather than adding a fourth overload used once.
            return ends.Length == 0
                ? $"Condition.{target.Item1}({Naming.Literal(target.Item2)}, {Naming.Literal(op)}, [{elements}])"
                : $"new Condition(Field: {Naming.Literal(target.Item2)}, Operator: {Naming.Literal(op)}, "
                    + $"Values: [{elements}]{ends})";
        }

        return value is null
            ? $"Condition.{target.Item1}({Naming.Literal(target.Item2)}, {Naming.Literal(op)})"
            : $"Condition.{target.Item1}({Naming.Literal(target.Item2)}, {Naming.Literal(op)}, {Naming.Literal(Scalar(value))})";
    }

    /// <summary>True when this rendered to something. Cheaper to read at a call site than comparing
    /// against the <c>null</c> literal.</summary>
    public static bool Present(JsonNode? condition) => Render(condition) is not null;

    private static string? Composite(string factory, JsonArray children)
    {
        var emitted = new List<string>(children.Count);

        foreach (var child in children.OfType<JsonObject>())
        {
            // One unwritable child sinks the whole thing. Emitting the others would produce a guard
            // that is real, compiles, and is more permissive than the definition — the worst of the
            // three possible outcomes, because it looks like it worked.
            if (Render(child) is not { } rendered) return null;
            emitted.Add(rendered);
        }

        // An `all` with nothing in it is not a guard, and saying so as "no condition" is right: an
        // empty conjunction is true, which is what no guard means.
        return emitted.Count == 0 ? null : $"Condition.{factory}({string.Join(", ", emitted)})";
    }

    /// <summary>
    /// One JSON value as the text a condition compares against.
    ///
    /// <para><c>ToString()</c> on a <c>JsonValue</c> would quote a string and leave a number bare,
    /// which is right — but it also renders <c>true</c> as <c>true</c> and a decimal through the
    /// current culture. Going via the invariant JSON text and stripping the quotes gives one answer
    /// on every machine, which is the property the whole generator rests on.</para>
    /// </summary>
    private static string Scalar(JsonNode? value) => value?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "",
        JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}
