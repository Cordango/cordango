// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cord;

/// <summary>
/// Everything Cord is willing to refuse — and the list is short on purpose.
///
/// <para><b>Rule 1: anything the Gate already says, Cord does not say.</b> The App Definition gate is
/// 3,181 lines and 351 semantic rules covering numeric compatibility, cycles, filter semantics,
/// process reachability, role resolution and the rest. Cord re-stating any of that would create a
/// second opinion about whether an application is valid, and two opinions diverge — slowly, silently,
/// and in a way nobody notices until they contradict each other in front of a user.</para>
///
/// <para>What is left is genuinely Cord's: the errors that exist because Cord has a vocabulary of its
/// own. "This aggregate names an entity that does not exist" and "I cannot tell which reference joins
/// these two records" are questions the gate cannot even ask, because by the time a document reaches
/// it those decisions have already been made. Everything else is lowered and handed over.</para>
///
/// <para><b>An enum, not a string.</b> A surface that must be enumerated cannot quietly grow to 351
/// rules: adding a member is a visible edit, and <c>CordCheckTests</c> asserts that every member is
/// produced by at least one test. A free-form message would let the same growth happen invisibly.</para>
/// </summary>
public enum CordErrorCode
{
    /// <summary>An operation names an entity that does not exist in the draft.</summary>
    UnknownEntity,

    /// <summary>An operation names a field that does not exist on its entity.</summary>
    UnknownField,

    /// <summary>Two entities would share a key, or an add would overwrite one.</summary>
    DuplicateEntity,

    /// <summary>Two fields on one entity would share a key.</summary>
    DuplicateField,

    /// <summary>An aggregate's <c>over</c> is neither <c>"mine"</c> nor a reference field on the
    /// entity holding the calculation.</summary>
    UnknownRelationship,

    /// <summary>Two or more references could join these entities, so the aggregate is a coin toss.
    /// Cord refuses to flip it — a join through the wrong reference is structurally valid, compiles,
    /// and quietly reports a wrong number.</summary>
    AmbiguousJoin,

    /// <summary>Nothing on the aggregated entity points where it would have to.</summary>
    UnresolvableJoin,

    /// <summary><c>count</c> was given a field, or another operation was not.</summary>
    AggregateFieldMismatch,

    /// <summary>The expression does not parse. Delegated verbatim to <c>ComputedExpr</c> — Cord owns
    /// no expression grammar of its own.</summary>
    InvalidExpression,

    /// <summary>A screen names an entity that does not exist.</summary>
    UnknownScreenEntity,

    /// <summary>A calendar section did not say WHICH date places a record on it.
    ///
    /// <para>Cord's, not the gate's: the renderer has a first-date-field fallback, so a calendar with
    /// no date is a perfectly valid document that answers the wrong question. The smoke run wrote
    /// "Decisions due" over an entity whose dates begin with <c>submitted_on</c>. A fallback is a last
    /// resort for a renderer; it is not authoring semantics.</para></summary>
    CalendarNeedsDateField,

    /// <summary>A governed field's default CONTRADICTS its lifecycle's initial state.
    ///
    /// <para>When the two agree the default is dropped in silence — it is the same fact twice. When
    /// they disagree they are two different claims about where a record starts, and choosing one would
    /// be Cord deciding something the author never did. Named here so the author settles it.</para>
    /// </summary>
    ConflictingInitialState,

    /// <summary>A <c>split</c> holding something that is not two pieces of content — in practice, another
    /// split. The schema fixes the COUNT at two; what it cannot say without a second copy of the whole
    /// section shape is that the two are content rather than more arrangement, and a copy would put the
    /// UI schema through its ceiling. So this is the one screen rule enforced by a check rather than by
    /// being unrepresentable, and the byte budget is the honest reason.
    ///
    /// <para>Nesting is refused rather than flattened because a split inside a split is a GRID, and a
    /// grid is the thing the two-up split was deliberately chosen over.</para></summary>
    NestedSplit,

    /// <summary>A removal names a screen the app does not have.</summary>
    UnknownScreen,

    /// <summary>This app's screens came in with a shape Cord could preserve but could not raise into
    /// independently editable semantics, so there is no way to change one without discarding the others.
    ///
    /// <para>A refusal instead of a choice between two data losses: the imported pages live in the raw
    /// overlay as one array with no Cord-side keys, so honouring the change means either the author's
    /// screen vanishing or every imported screen vanishing. Both shipped once. This is the wall plan
    /// risk 3 asks for — visible, named, and incapable of destroying anything — and it shrinks as
    /// <c>CordImport</c> learns more page shapes.</para></summary>
    ImportedScreensNotEditable,

    /// <summary>A removal names a lifecycle, action, automation or role the app does not have.
    ///
    /// <para>Worth refusing rather than treating as a no-op: an author removing something that is not
    /// there has either misremembered the key or is working from a stale idea of the draft, and both are
    /// better answered now than by an app quietly keeping behaviour somebody meant to delete.</para>
    /// </summary>
    UnknownBehaviour,

    /// <summary>A transition moves to or from a state its own lifecycle does not declare.
    ///
    /// <para>Cord-level rather than a restatement of the gate: states and transitions arrive in ONE
    /// operation here, so a mistyped state is a mistake inside a single statement the author wrote, and
    /// saying so at the operation index beats a pointer into a generated document.</para></summary>
    UnknownState,

    /// <summary>An operation names a tab its screen does not have.</summary>
    UnknownTab,

    /// <summary>Two tabs on one screen share a key, so one of them cannot be addressed.</summary>
    DuplicateTab,

    /// <summary>The operation names an aggregate outside the open scope.
    ///
    /// <para>The mechanical form of "revising one screen must not alter another". A co-creation
    /// candidate is scoped to exactly one aggregate, and the user accepts <b>that</b> aggregate — so an
    /// operation reaching past it would put changes nobody reviewed inside something they did approve.
    /// Refused rather than split into "the parts we accept and the parts we don't", because a partly
    /// applied batch is a state no author asked for.</para></summary>
    OutsideScope,

    /// <summary>The operation could not be read at all: a missing argument, an unknown <c>op</c>, a
    /// value of the wrong shape.</summary>
    MalformedOperation,
}

/// <summary>One refusal, in Cord's vocabulary rather than the App Definition's.</summary>
/// <param name="Where">A Cord path — <c>entities/period/fields/payroll_cost</c>. Not a JSON Pointer
/// into a document the author never wrote.</param>
/// <param name="OperationIndex">Which operation in the batch caused this, so a rejected change names
/// the line to fix rather than the whole submission.</param>
/// <param name="Candidates">For <see cref="CordErrorCode.AmbiguousJoin"/>: the references that could
/// have been meant. Naming them is the difference between a question the author can answer and one
/// they cannot.</param>
public sealed record CordError(
    CordErrorCode Code,
    string Where,
    string Message,
    int? OperationIndex = null,
    IReadOnlyList<string>? Candidates = null)
{
    public override string ToString()
    {
        var op = OperationIndex is { } i ? $"op[{i}] " : "";
        var also = Candidates is { Count: > 0 } c ? $" (candidates: {string.Join(", ", c)})" : "";
        return $"{op}{Code} at {Where}: {Message}{also}";
    }
}
