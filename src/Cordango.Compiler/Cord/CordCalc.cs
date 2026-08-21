// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>A calculated field: arithmetic over this record, or an aggregate over records that point
/// at it. Exactly one of the two, mirroring the App Definition's own rule.</summary>
public abstract record CordCalc;

/// <summary>
/// Arithmetic over this record — passed through to <c>computed.expr</c> VERBATIM.
///
/// <para>Deliberately not re-modelled. <c>ComputedExpr</c> is 542 tested lines that already know
/// <c>prev(field, seed)</c>, <c>pow</c>, duration functions, typed comparison and the acyclicity rule.
/// A second grammar here would be a regression dressed as a feature, and it would have to be kept in
/// step with the first one forever.</para>
///
/// <para>The honest consequence: Cord adds no help for <c>prev()</c> and series either. The hardest
/// construct in the corpus stays exactly as hard, and the calculation grammar's win is entirely on
/// the aggregate side.</para>
/// </summary>
public sealed record CordExpr(string Expr) : CordCalc;

/// <summary>
/// An aggregate over other records — what lowers to the <c>rollup</c> machinery.
///
/// <para>The App Definition spells this as <c>{entity, via, match?, window?, op, field?, filters?}</c>,
/// and the hard part was never the syntax: it is a four-way modelling choice about which side of the
/// relationship each field lives on. Three of the four disappear here.</para>
///
/// <para><b><see cref="Via"/> is inferred</b> (see <see cref="CordRefIndex"/>) — uniquely, on all 45
/// aggregates in the corpus. The gate rule about what makes two records siblings becomes unreachable
/// rather than merely discouraged, because the author never writes the field it is about.</para>
///
/// <para><b><see cref="During"/> names its own direction</b> — see <see cref="CordWindow"/>.</para>
///
/// <para><b>Dates versus numbers needs no syntax at all.</b> It falls out of the field types, and the
/// gate already enforces that a window compares like with like. This is the strongest argument for a
/// structured shape over a string grammar: a string would have been tempted to spell <c>2026-01</c>
/// and <c>6</c> differently, and then "the months this hire covers" and "the first six periods" —
/// the same question at two scales — would need two spellings.</para>
/// </summary>
/// <param name="Op">count | sum | avg | min | max.</param>
/// <param name="Of">The entity being aggregated.</param>
/// <param name="Field">The numeric field on it. Absent exactly when <paramref name="Op"/> is count.</param>
/// <param name="Over"><c>"mine"</c> for records that point at THIS record; otherwise the key of a
/// reference field on THIS entity, meaning records that point at whatever it points at — a SIBLING
/// aggregation. A period sums the hiring lines of its own scenario: neither owns the other, they share
/// a parent.</param>
/// <param name="Via">Normally null. Set only where inference would be ambiguous, or would not
/// reproduce what the source document said — which is what keeps the round-trip exact without letting
/// the coverage number claim an inference that did not happen.</param>
public sealed record CordAggregate(
    string Op,
    string Of,
    string? Field,
    string Over,
    string? Via = null,
    CordWindow? During = null,
    IReadOnlyList<CordFilter>? Where = null) : CordCalc
{
    /// <summary><see cref="Over"/>'s value for "records that point directly at me".</summary>
    public const string Mine = "mine";
}

/// <summary>
/// A declarative SUMIFS. Two directions, and the variant name is which one.
///
/// <para>The schema needs about seven hundred characters to explain <c>from</c>/<c>to</c>/<c>against</c>
/// versus <c>at</c>/<c>within</c>, because in each spelling the reader has to work out which side of
/// the relationship every field lives on. Here the type says it: whatever is named "my" is on the
/// record being computed, everything else is on the rows being aggregated. Naming both directions at
/// once — a real gate error — stops being expressible.</para>
/// </summary>
public abstract record CordWindow;

/// <summary>Rows whose RANGE covers a point on my record: a hire being paid in this month.</summary>
/// <param name="From">Start of the row's range.</param>
/// <param name="To">End of the row's range. Absent means OPEN — an open-ended hire is still on the
/// payroll, never excluded.</param>
/// <param name="MyPoint">The date or number on MY record the range must cover.</param>
public sealed record CordCovering(string From, string? To, string MyPoint) : CordWindow;

/// <summary>Rows whose single DATE falls inside a range on my record: a funding round landing in the
/// month it closes.</summary>
/// <param name="At">The row's own date or number.</param>
/// <param name="MyFrom">Start of MY range.</param>
/// <param name="MyTo">End of MY range. Absent means OPEN.</param>
public sealed record CordInside(string At, string MyFrom, string? MyTo) : CordWindow;

/// <summary>One narrowing condition on an aggregate — how a fee rate splits by basis.</summary>
public sealed record CordFilter(string? Field, string? Operator, JsonNode? Value, JsonObject? Raw = null);
