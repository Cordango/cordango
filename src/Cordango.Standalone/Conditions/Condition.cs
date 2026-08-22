// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Conditions;

/// <summary>
/// A question about a record that answers yes or no: a command's guard, a workflow's <c>when</c>, a
/// filter on a rollup.
///
/// <para>One shape for all three, because the definition uses one shape for all three. A tree of
/// <c>all</c> / <c>any</c> / <c>not</c> over leaves that name a field, an operator and what to
/// compare it to.</para>
///
/// <para><b>The expected value is a string, and the record's value is not.</b> That asymmetry is
/// deliberate: what a definition writes is text — <c>"closed"</c>, <c>"6"</c>, <c>"{{today+7}}"</c> —
/// while what a record holds is typed JSON. <see cref="ConditionEvaluator"/> compares them
/// numerically when both sides read as numbers and ordinally otherwise, so <c>value: 6</c> in the
/// definition and a decimal column in the database meet correctly without the generated code having
/// to carry JSON literals around.</para>
/// </summary>
/// <param name="Field">The field to read. Null on a composite.</param>
/// <param name="Operator">One of the thirteen the language defines. Null on a composite.</param>
/// <param name="Value">What to compare against, for the operators that take one value.</param>
/// <param name="Values">What to compare against, for <c>in</c>, <c>notIn</c>, <c>between</c> and
/// <c>overlaps</c>.</param>
/// <param name="EndField">The other end of the record's own range, for <c>overlaps</c>.</param>
/// <param name="Path">A one-hop reference into another record — <c>room.requires_approval</c> —
/// evaluated when the caller supplies a hop and false when it cannot.</param>
/// <param name="All">Every child must hold.</param>
/// <param name="Any">At least one child must hold.</param>
/// <param name="Not">The child must not hold.</param>
public sealed record Condition(
    string? Field = null,
    string? Operator = null,
    string? Value = null,
    IReadOnlyList<string>? Values = null,
    string? EndField = null,
    string? Path = null,
    IReadOnlyList<Condition>? All = null,
    IReadOnlyList<Condition>? Any = null,
    Condition? Not = null)
{
    /// <summary>A leaf reading one of the record's own fields.</summary>
    public static Condition Leaf(string field, string @operator, string? value = null) =>
        new(Field: field, Operator: @operator, Value: value);

    /// <summary>A leaf whose expected side is a list: <c>in</c>, <c>notIn</c>, <c>between</c>.</summary>
    public static Condition Leaf(string field, string @operator, IReadOnlyList<string> values) =>
        new(Field: field, Operator: @operator, Values: values);

    /// <summary>A leaf reading a field on a referenced record.</summary>
    public static Condition Hop(string path, string @operator, string? value = null) =>
        new(Path: path, Operator: @operator, Value: value);

    public static Condition Every(params Condition[] children) => new(All: children);

    public static Condition Some(params Condition[] children) => new(Any: children);

    public static Condition Never(Condition child) => new(Not: child);
}
