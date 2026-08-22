// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Records;

/// <summary>
/// The arithmetic a computed field is made of, where plain C# operators would answer differently
/// from the definition.
///
/// <para><b>This is not an expression engine.</b> A computed field is emitted as a real C# method —
/// <c>(r.Revenue ?? 0m) - (r.Costs ?? 0m)</c> — and most of it needs nothing from here. What does is
/// the handful of places where the language has a rule and C# has a different one, and each of those
/// is a method rather than an inline expression because a rule repeated at forty call sites is a
/// rule that will eventually be repeated wrong.</para>
///
/// <para><b>The rule underneath all of them: unknown is not zero and not false.</b> A blank NUMBER
/// field reads as zero, because a record with no tax has a tax of nothing and a total that refused
/// to add it up would be blank on every half-filled row. But a figure that could not be WORKED OUT —
/// divided by zero, compared against a blank, raised to a power that overflows — is unknown, and it
/// stays unknown through everything downstream. A cap computed from an unknown bound must not
/// silently become no cap at all.</para>
/// </summary>
public static class Calc
{
    /// <summary>
    /// Division, which is the one arithmetic operator that can fail to have an answer.
    ///
    /// <para><c>x / 0</c> is neither zero nor an exception here: it is unknown. A runway of "cash
    /// divided by monthly burn" on a plan with no costs yet is not infinite and not zero — nobody
    /// knows it — and a plan that showed 0 months of runway because nothing had been entered would
    /// be alarming and wrong.</para>
    /// </summary>
    public static decimal? Divide(decimal? left, decimal? right) =>
        right is null or 0m ? null : left / right;

    /// <summary>The lower of two figures — a usage charge capped at a plan's ceiling. Null in, null
    /// out: a bound that could not be worked out is not the same as no bound, and returning the
    /// other side would silently un-cap the figure.</summary>
    public static decimal? Min(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? Math.Min(a, b) : null;

    /// <summary>The higher of two figures — a balance floored at zero. Same discipline as
    /// <see cref="Min"/>.</summary>
    public static decimal? Max(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? Math.Max(a, b) : null;

    /// <summary>
    /// Raising to a power, for compound growth and annualised returns.
    ///
    /// <para>Through <c>double</c>, because <c>decimal</c> has no power operator — and back through
    /// a checked conversion, because the round trip can overflow and an OverflowException from
    /// inside a total is a 500 on a page somebody was only reading.</para>
    /// </summary>
    public static decimal? Power(decimal? value, decimal? exponent)
    {
        if (value is not { } v || exponent is not { } e) return null;

        var result = Math.Pow((double)v, (double)e);
        if (double.IsNaN(result) || double.IsInfinity(result)) return null;

        try
        {
            return (decimal)result;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// An ordered comparison that can answer "cannot say".
    ///
    /// <para>A bare <c>&lt;</c> on two nullables answers FALSE when either is null, which reads as
    /// "no" — so a rule keyed on "is the balance below the threshold" would fire on every record
    /// whose balance has not been computed yet. Here that is null, and a null never satisfies
    /// anything.</para>
    /// </summary>
    public static bool? Compare(decimal? left, decimal? right, string op)
    {
        if (left is not { } a || right is not { } b) return null;

        return op switch
        {
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            _ => null,
        };
    }

    /// <summary>Equality that keeps "cannot say" separate from "no". Two unknowns are not equal —
    /// they are two things nobody knows.</summary>
    public static bool? Same(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? a == b : null;

    public static bool? Different(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? a != b : null;

    public static bool? Same(bool? left, bool? right) =>
        left is { } a && right is { } b ? a == b : null;

    public static bool? Different(bool? left, bool? right) =>
        left is { } a && right is { } b ? a != b : null;

    /// <summary>Whole and fractional days between two instants, the way the definition means it:
    /// null unless both ends are known.</summary>
    public static decimal? Days(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalDays : null;

    public static decimal? Hours(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalHours : null;

    public static decimal? Minutes(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalMinutes : null;

    /// <summary>
    /// The same three durations over plain dates.
    ///
    /// <para>A <c>date</c> column is a <see cref="DateOnly"/> and a <c>datetime</c> is a
    /// <see cref="DateTimeOffset"/>, and a definition happily writes <c>days_between</c> over either.
    /// Converting at midnight UTC keeps "two days" meaning two days rather than one and a bit.</para>
    /// </summary>
    public static decimal? Days(DateOnly? from, DateOnly? to) =>
        from is { } a && to is { } b ? b.DayNumber - a.DayNumber : null;

    public static decimal? Hours(DateOnly? from, DateOnly? to) => Days(from, to) * 24m;

    public static decimal? Minutes(DateOnly? from, DateOnly? to) => Days(from, to) * 1440m;
}
