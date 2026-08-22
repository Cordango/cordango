// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using Cordango.Definition;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// A computed expression as C#.
///
/// <para>The definition writes <c>net_result = total_revenue - total_costs - total_tax</c>, and this
/// writes:</para>
/// <code>
/// public static decimal? NetResult(Scenario r) =>
///     (r.TotalRevenue ?? 0m) - (r.TotalCosts ?? 0m) - (r.TotalTax ?? 0m);
/// </code>
///
/// <para><b>Code, not data — the opposite call from guards and workflows, and for a reason.</b> A
/// guard is a small tree the runtime walks a handful of times per request. A computed field is
/// arithmetic that runs on every write and, through rollups, on every ancestor of every write; the
/// platform needed two rounds of optimisation before a grid recalculated in under a minute. More
/// than that: a total that comes out wrong is the thing a person most needs to be able to READ, and
/// a method they can set a breakpoint in beats an expression string handed to an interpreter.</para>
///
/// <para><b>The null discipline is the whole difficulty.</b> A blank NUMBER field reads as zero — a
/// record with no tax has a tax of nothing, and refusing to add it up would make every total on a
/// half-filled row blank. But a division by zero, or by a blank, is NOT zero and NOT an error: it is
/// unknown, and it stays unknown all the way out. Same for <c>min</c>, <c>max</c> and <c>pow</c>
/// where either side cannot be worked out — returning the other side would silently un-cap a figure
/// that was supposed to be capped. Every one of those rules is the evaluator's, mirrored here,
/// because the platform and a generated application computing different totals from one definition
/// is the worst failure this project has.</para>
/// </summary>
public static class ComputedEmitter
{
    /// <summary>
    /// The C# for one expression, or null when it cannot be written.
    ///
    /// <para>Null is never "emit nothing and carry on". The caller reports it, because a computed
    /// column that silently stays empty looks exactly like one whose inputs are empty.</para>
    /// </summary>
    public static string? Expression(EntityModel entity, FieldModel field)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(field);

        var expr = AppModel.Str(field.Computed?["expr"]);
        if (expr is null) return null;

        var node = ComputedExpr.Parse(expr, key => Kind(entity, key));
        return node is null ? null : Render(entity, node);
    }

    /// <summary>
    /// What TYPE an identifier has, which decides what its comparisons mean.
    ///
    /// <para>An unknown identifier is a number rather than an error, matching the evaluator: the gate
    /// has already refused an expression naming a field that does not exist, so anything reaching
    /// here that is not on the entity is a hop into another record, and those are numbers until
    /// hops are generated.</para>
    /// </summary>
    private static ComputedValueKind Kind(EntityModel entity, string key) =>
        entity.Field(key)?.Type switch
        {
            "boolean" => ComputedValueKind.Boolean,
            "date" or "datetime" => ComputedValueKind.Date,
            _ => ComputedValueKind.Number,
        };

    private static string? Render(EntityModel entity, ComputedExpr.Node node) => node switch
    {
        // `1` rather than `1M`: the suffix is only needed where the literal stands alone, and every
        // literal here is in arithmetic with a decimal. Written invariantly, so a machine with a
        // comma for a decimal point does not emit `1,5`.
        ComputedExpr.NumberNode n => Literal(n.Value),

        ComputedExpr.BooleanNode b => b.Value ? "true" : "false",

        ComputedExpr.FieldNode f => Field(entity, f),

        ComputedExpr.UnaryNode { Op: "-" } u => Render(entity, u.Operand) is { } operand ? $"-{operand}" : null,
        ComputedExpr.UnaryNode u => Render(entity, u.Operand) is { } operand ? $"!{operand}" : null,

        ComputedExpr.BinaryNode b => Binary(entity, b),

        // Helpers rather than inline expressions, because each carries a rule that would have to be
        // repeated — and would eventually be repeated WRONG — at every use.
        //
        // BOTH sides checked. Checking only the left emitted `Calc.Max(1m, )` for
        // `max(1, segment.shared_apps * point.adoption)`, where the literal renders and the hop does
        // not — an application that did not compile, from an expression the definition was entitled
        // to write. Half an expression is never worth emitting: the caller reports it and the column
        // stays empty, which is a gap somebody can see.
        ComputedExpr.FunctionNode f => Pair(entity, f.Left, f.Right) is (
                { } functionLeft, { } functionRight)
            ? $"Calc.{f.Name switch { "pow" => "Power", "min" => "Min", _ => "Max" }}({functionLeft}, {functionRight})"
            : null,

        ComputedExpr.DurationNode d => Read(entity, d.From) is { } from && Read(entity, d.To) is { } to
            ? $"Calc.{d.Name switch { "minutes_between" => "Minutes", "hours_between" => "Hours", _ => "Days" }}({from}, {to})"
            : null,

        // prev() reads the row before this one in an ordered series, which needs the series. Not
        // written yet, and reported rather than guessed at.
        ComputedExpr.PrevNode => null,

        _ => null,
    };

    private static string? Binary(EntityModel entity, ComputedExpr.BinaryNode node)
    {
        var (left, right) = Pair(entity, node.Left, node.Right);
        if (left is null || right is null) return null;

        return node.Op switch
        {
            // Division is the one arithmetic operator that can fail to have an answer. `x / 0` is
            // not zero and not an exception: it is unknown, and it propagates.
            "/" => $"Calc.Divide({left}, {right})",

            "+" or "-" or "*" => $"({left} {node.Op} {right})",

            // `&` and `|`, NOT `&&` and `||`, and this is not a style choice twice over.
            //
            // It did not compile. A boolean computed field is `bool?` the moment a comparison is
            // involved, because `Calc.Compare` can answer "cannot say" — and `&&` is not defined on
            // `bool?` in C#, so `((a / b) < 1) and true` emitted CS0019 and the whole application
            // failed to build. No corpus application has a computed field combining a comparison with
            // boolean logic, which is the only reason this was not already loud.
            //
            // And the operators it does compile to are the right ones anyway: `&` and `|` on `bool?`
            // are three-valued logic. Unknown AND false is false whatever the unknown turns out to be;
            // unknown OR true is true. The answer only goes unknown where the unknown could change it.
            "and" => $"({left} & {right})",
            "or" => $"({left} | {right})",

            // A comparison where either side is unknown is unknown, not false. `Calc.Compare` keeps
            // that; a bare `<` on two nullables would quietly answer false and make a guard on an
            // uncomputed row read as "does not qualify" rather than "cannot say".
            "<" or "<=" or ">" or ">=" => $"Calc.Compare({left}, {right}, \"{node.Op}\")",

            "==" => $"Calc.Same({left}, {right})",
            "!=" => $"Calc.Different({left}, {right})",

            _ => null,
        };
    }

    /// <summary>
    /// Reading one field, in whatever way that field can actually be read.
    ///
    /// <para><b>A blank number is zero and a blank boolean is false</b> — a record with no tax has a
    /// tax of nothing, and a total that refused to add it up would be blank on every half-filled
    /// row. A blank DATE stays null: there is no sensible zero for a date, and inventing one would
    /// make a duration from a missing date read as decades rather than as unknown.</para>
    ///
    /// <para><b>A REQUIRED field is not nullable, so it gets neither.</b> <c>r.Months ?? 0m</c> does
    /// not compile against a <c>long</c>, and emitting it anyway produced an application that did
    /// not build from an expression the definition was entitled to write.</para>
    ///
    /// <para><b>And an integer is cast, which is the subtle one.</b> Two required <c>long</c> columns
    /// multiplied and divided in C# do INTEGER arithmetic — <c>done * 100 / total</c> would truncate
    /// at every step and disagree with the platform, which works in decimal throughout. The cast is
    /// there so the arithmetic is the definition's arithmetic, and it is only emitted where it
    /// changes something.</para>
    /// </summary>
    private static string? Field(EntityModel entity, ComputedExpr.FieldNode node)
    {
        if (entity.Field(node.Key) is not { } field || Read(entity, node.Key) is not { } read) return null;

        return (node.FieldKind, field.Required) switch
        {
            (ComputedValueKind.Number, false) => $"({read} ?? 0m)",
            (ComputedValueKind.Number, true) when field.ClrType == "long" => $"((decimal){read})",
            (ComputedValueKind.Number, true) => read,

            (ComputedValueKind.Boolean, false) => $"({read} ?? false)",

            _ => read,
        };
    }

    private static (string? Left, string? Right) Pair(EntityModel entity, ComputedExpr.Node left, ComputedExpr.Node right) =>
        (Render(entity, left), Render(entity, right));

    /// <summary>
    /// Reading one field off the record.
    ///
    /// <para>Null when the entity does not have it — a hop into another record, which is a later
    /// slice. Reported by the caller rather than emitted as something that compiles and is
    /// wrong.</para>
    /// </summary>
    private static string? Read(EntityModel entity, string key) =>
        entity.Field(key) is { } field ? "r." + field.PropertyName : null;

    /// <summary>Invariant, and always with a decimal point, so the emitted arithmetic is decimal
    /// arithmetic rather than integer division dressed up as it.</summary>
    private static string Literal(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) ? text + "m" : text + "m";
    }
}
