// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// A change, prepared but not committed.
/// </summary>
/// <param name="Next">The draft as it WOULD be. Becomes the draft only if the caller's validator
/// accepts <paramref name="Lowered"/>. (Named <c>Next</c> rather than <c>Clone</c> because C# reserves
/// that member name on records.)</param>
/// <param name="Lowered">The App Definition to hand to the existing pipeline.</param>
/// <param name="Errors">Cord's own refusals. Non-empty means <paramref name="Lowered"/> is not worth
/// validating.</param>
/// <param name="Map">Rewrites App Definition pointers in the validator's errors back into Cord paths,
/// so a refusal names something the author actually wrote.</param>
public sealed record PreparedChange(
    CordApp Next,
    JsonNode Lowered,
    IReadOnlyList<CordError> Errors,
    CordPointerMap Map)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Applies operations to a copy of the draft and lowers the result — <b>and stops there</b>.
///
/// <para><b>Why it stops there.</b> Rule 0: Cord may not reference the Generator, so it cannot know
/// <c>IDesignValidator</c> exists, let alone call it. That is not a limitation to work around — it is
/// the boundary that keeps Cord extractable. Cord contributes a pure function; the session sequences
/// it:</para>
///
/// <code>
/// p = CordTransaction.Prepare(draft, ops)      // here, pure
/// if (!p.Ok) → report p.Errors, draft unchanged
/// v = await validator.ValidateAsync(p.Lowered) // the caller, the EXISTING seam
/// if (v.Coherent) draft = p.Next               // gate-clean and compiles — keep it
/// if (v.Valid)    publishable[v.Hash] = …      // finished as well — only THIS may be published
/// </code>
///
/// <para>An open-source consumer gets <c>Prepare</c> and brings their own validator; Cordango brings
/// <c>CandidateValidator</c>. Same function, and there is still exactly one validator interface in the
/// codebase rather than a second one invented for Cord.</para>
///
/// <para><b>The invariant the caller must keep — TWO thresholds, not one.</b> An earlier version of this
/// note said the draft is always either empty or a state that passed the COMPLETE pipeline. That was
/// true while a single call carried the whole application and became unsatisfiable once concerns got
/// their own tools: a domain authored before its screens cannot pass the finished-product checks, so
/// requiring it discarded every intermediate step and no application could be built at all.</para>
///
/// <para>What replaces it:</para>
/// <list type="number">
/// <item><b>To enter the draft</b> — the change must be COHERENT: gate-clean and compiling. Only the
/// finished-product checks may fail. <see cref="CordCheck"/> is emphatically NOT sufficient here; it
/// deliberately omits nearly every gate rule (rule 1), so "Cord could read it" would let a gate-invalid
/// change poison what the next turn edits.</item>
/// <item><b>To be published</b> — the change must additionally be VALID, and publication is authorised
/// only by a hash the validator issued on that pass. Unchanged, and unreachable from here.</item>
/// </list>
///
/// <para>So a draft may become INCOMPLETE. It may never become INCOHERENT, and it can never be
/// published by being either. That is why <see cref="PreparedChange.Next"/> is returned separately
/// instead of the draft being mutated here: the decision to keep it is the caller's, and the caller is
/// the only party that can ask a validator anything.</para>
/// </summary>
public static class CordTransaction
{
    /// <param name="allowed">The operation names the calling tool offers, so a concern-scoped tool
    /// cannot be used to reach another concern's vocabulary. Null accepts every operation.</param>
    public static PreparedChange Prepare(CordApp draft, JsonNode? ops,
        IReadOnlyCollection<string>? allowed = null)
    {
        var (parsed, parseErrors) = CordOps.Parse(ops, allowed);
        if (parseErrors.Count > 0)
            return new PreparedChange(draft, new JsonObject(), parseErrors, CordPointerMap.Empty);

        return Prepare(draft, parsed);
    }

    public static PreparedChange Prepare(CordApp draft, IReadOnlyList<CordOp> ops)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(ops);

        var (clone, applyErrors) = CordOps.Apply(draft, ops);
        if (applyErrors.Count > 0)
            return new PreparedChange(draft, new JsonObject(), applyErrors, CordPointerMap.Empty);

        // Cord's own checks, and only those. Everything about whether the resulting APPLICATION is
        // correct belongs to the gate on the lowered document — see rule 1.
        var checks = CordCheck.Errors(clone);
        if (checks.Count > 0)
            return new PreparedChange(draft, new JsonObject(), checks, CordPointerMap.Empty);

        var lowering = CordLower.Lowering(clone);
        return new PreparedChange(clone, lowering.Definition, [], CordPointerMap.From(clone, lowering));
    }
}
