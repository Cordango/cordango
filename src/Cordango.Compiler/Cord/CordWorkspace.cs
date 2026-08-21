// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cord;

/// <summary>What one <see cref="CordWorkspace.Apply"/> produced — Cord's half of the verdict.</summary>
/// <param name="Applied">The operations parsed, named aggregates inside the open scope, applied to a
/// copy and passed Cord's own checks. NOT coherence: whether the lowered document survives the gate
/// and compiles is the validator's question, which rule 0 keeps out of this assembly. A host keeps
/// <see cref="NextCandidate"/> only after its validator says coherent.</param>
/// <param name="Previewable">Today equal to <paramref name="Applied"/>; separate because the two may
/// diverge (an aggregate can be sound while the composed preview fails for a reason the aggregate
/// did not cause), and collapsing them now would make that future change a breaking one.</param>
/// <param name="NextCandidate">The composed draft as it WOULD be. Null when refused — a refused
/// change must leave nothing behind to accidentally keep.</param>
/// <param name="Lowered">The App Definition for the validator. Null when refused.</param>
public sealed record CordWorkspaceChange(
    bool Applied,
    bool Previewable,
    IReadOnlyList<CordError> Errors,
    CordPointerMap Map,
    CordApp? NextCandidate,
    JsonNode? Lowered)
{
    public bool Ok => Applied;
}

/// <summary>
/// The accepted baseline, at most one working candidate, and the rules connecting them — gap G4's
/// missing owner, pure and synchronous so the hosted Studio and the OSS CLI can share it unchanged.
///
/// <para><b>What it holds:</b> <see cref="Accepted"/> is the app as the user has approved it — a
/// <see cref="CordApp"/> only ever changed by an accepted operation. <see cref="Candidate"/> is the
/// composed draft (baseline + the one candidate aggregate's operations), or null when nothing is
/// open. <see cref="Scope"/> is the one aggregate the candidate may touch; <see cref="Apply"/>
/// refuses an operation naming anything else, which is the mechanical form of "feedback updates only
/// that same screen".</para>
///
/// <para><b>What it must not do:</b> call a model, know a provider, resolve a tenant, touch a
/// database, or decide who may accept. Rule 0 — this assembly references Definition and the BCL
/// only — is what keeps one implementation serving both hosts, and <c>BoundaryTests</c> enforces
/// it.</para>
///
/// <para><b>There is deliberately no Accept here.</b> Acceptance is a host action (an HTTP endpoint,
/// a typed CLI command) authorised by a user; the workspace contributes only the
/// <see cref="ReviewHash"/> the host issues and later compares. A type that could accept on its own
/// behalf would be one tool-wiring mistake away from a model accepting its own work.</para>
/// </summary>
/// <param name="BaselineIncomplete">The baseline journal did not replay whole. HARD FAIL: a partial
/// baseline must never be authored over, materialized or published — the missing suffix would be
/// silently un-built. <see cref="Apply"/> throws rather than working over one.</param>
/// <param name="CandidateDropped">The candidate journal did not replay whole, so the candidate was
/// dropped rather than half-kept. A candidate is cheap; half of one is dangerous. The host records a
/// status exchange and re-opens the aggregate.</param>
public sealed record CordWorkspace(
    CordApp Accepted,
    CordApp? Candidate,
    CordAggregateRef? Scope,
    bool BaselineIncomplete = false,
    bool CandidateDropped = false)
{
    /// <summary>
    /// Rebuilds a workspace from its two journals — the resume path, spending zero model calls.
    ///
    /// <para>Two replays with two different failure answers, per cordPlan §9: an incomplete BASELINE
    /// poisons everything built on it, so it is a flag the host must treat as fatal; an incomplete
    /// CANDIDATE is discarded — the batches before the gap describe a draft the session had already
    /// superseded, and replaying a prefix would present work nobody saw as the current
    /// candidate.</para>
    /// </summary>
    /// <param name="identity">The starting model. For a Cord-created app this is its identity before
    /// the journal; for an imported AppDefinition it may be the complete <see cref="CordImport"/>
    /// result used as an immutable origin, with accepted operations replayed on top.</param>
    /// <param name="baselineBatches">Accepted operation batches, oldest first.</param>
    /// <param name="candidateBatches">The open candidate's operation batches, oldest first, applied
    /// on top of the baseline. Null or empty when no candidate is open.</param>
    public static CordWorkspace Load(CordApp identity, IEnumerable<JsonNode?> baselineBatches,
        CordAggregateRef? scope = null, IEnumerable<JsonNode?>? candidateBatches = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(baselineBatches);

        var baseline = CordJournal.Replay(identity, baselineBatches);
        if (!baseline.Complete)
            return new CordWorkspace(baseline.Draft, null, scope, BaselineIncomplete: true);

        var batches = candidateBatches?.ToList();
        if (batches is not { Count: > 0 })
            return new CordWorkspace(baseline.Draft, null, scope);

        var candidate = CordJournal.Replay(baseline.Draft, batches);
        return candidate.Complete
            ? new CordWorkspace(baseline.Draft, candidate.Draft, scope)
            : new CordWorkspace(baseline.Draft, null, scope, CandidateDropped: true);
    }

    /// <summary>The preview subject: the baseline with the candidate overlaid when one is open,
    /// the baseline alone otherwise. Never the candidate alone.</summary>
    public CordApp Composed
    {
        get
        {
            Usable();
            return Candidate ?? Accepted;
        }
    }

    /// <summary>
    /// Refuses every read of a workspace whose baseline did not replay whole.
    ///
    /// <para>An incomplete baseline has NO valid representation, so there is nothing safe to do with
    /// it: lowering it, hashing it, previewing it or writing files from it all publish an application
    /// that is silently missing whatever the replay could not read. One guard on every exit rather
    /// than a flag each caller is trusted to check — the flag was there before, and
    /// <see cref="Apply"/> was the only path that honoured it.</para>
    /// </summary>
    private void Usable()
    {
        if (BaselineIncomplete)
            throw new InvalidOperationException(
                "the baseline journal did not replay whole — this workspace cannot be lowered, "
                + "hashed, previewed or materialized until the journal is repaired");
    }

    /// <summary>
    /// One model change, prepared against the composed draft — and refused whole if any operation
    /// names an aggregate outside the open scope.
    ///
    /// <para>Scope refusal happens BEFORE <see cref="CordTransaction.Prepare"/> and is atomic with
    /// the batch, for the same reason the parser refuses a whole batch on one malformed op: a
    /// half-admitted change is half of an application nobody designed. Nothing here mutates — the
    /// caller keeps <see cref="CordWorkspaceChange.NextCandidate"/> only after its validator accepts
    /// the lowered document, mirroring the draft-advances-on-coherence rule the design session
    /// already lives by.</para>
    /// </summary>
    public CordWorkspaceChange Apply(JsonNode? ops)
    {
        Usable();

        if (Scope is null)
            return Refused(new CordError(CordErrorCode.OutsideScope, "workspace",
                "no aggregate is open — select one before applying changes"));

        var (parsed, parseErrors) = CordOps.Parse(ops, CordAggregates.AllowedOps(Scope));
        if (parseErrors.Count > 0) return Refused([.. parseErrors]);

        var outside = new List<CordError>();
        for (var i = 0; i < parsed.Count; i++)
        {
            var target = CordAggregates.Target(parsed[i]);
            if (!CordAggregates.Admits(Scope, target))
                outside.Add(new CordError(CordErrorCode.OutsideScope, target.ToString(),
                    $"this change is scoped to {Scope} — '{target}' is a different aggregate, and "
                    + "editing it here would touch work outside what is under review", i));
        }
        if (outside.Count > 0) return Refused([.. outside]);

        var prepared = CordTransaction.Prepare(Composed, parsed);
        return prepared.Ok
            ? new CordWorkspaceChange(true, true, [], prepared.Map, prepared.Next, prepared.Lowered)
            : Refused([.. prepared.Errors]);

        static CordWorkspaceChange Refused(params IReadOnlyList<CordError> errors) =>
            new(false, false, errors, CordPointerMap.Empty, null, null);
    }

    /// <summary>Adopt a prepared candidate — called by the host AFTER its validator reported the
    /// lowered document coherent, never before.</summary>
    public CordWorkspace WithCandidate(CordApp next) => this with { Candidate = next };

    /// <summary>
    /// Open an aggregate for editing.
    ///
    /// <para><b>Refused while a candidate for a DIFFERENT aggregate is open</b>, and that refusal is
    /// the mechanical form of "one candidate, one aggregate". Carrying the candidate across a scope
    /// change would leave operations from the old aggregate inside the composed draft, so a later
    /// accept — which the user gives for the aggregate they are looking at — would write changes they
    /// never reviewed into something they did approve. Discard or accept first; both are explicit,
    /// and neither can happen by accident.</para>
    ///
    /// <para>Re-selecting the SAME aggregate is how a revision turn re-opens its own candidate, so
    /// that case is allowed and keeps the candidate.</para>
    /// </summary>
    public CordWorkspace Select(CordAggregateRef? scope)
    {
        if (Candidate is not null && scope != Scope)
            throw new InvalidOperationException(
                $"a candidate for {Scope?.ToString() ?? "an aggregate"} is still open — accept or "
                + "discard it before opening " + (scope?.ToString() ?? "nothing"));

        return this with { Scope = scope };
    }

    /// <summary>Forget the candidate. Restoration is a no-op by construction — the baseline was
    /// never written, so there is nothing to reverse, which is the strongest version of
    /// "discard restores byte-for-byte" available.</summary>
    public CordWorkspace Discard() => this with { Candidate = null };

    /// <summary>The composed document, lowered. What the validator judges and the preview compiles.</summary>
    public JsonNode Lowered()
    {
        Usable();
        return CordLower.Lower(Composed);
    }

    /// <summary>
    /// The hash acceptance binds to: <c>DefinitionHash</c> of the lowered composed document.
    ///
    /// <para>Issued durably by the host when the candidate is COHERENT. Deliberately not
    /// <c>CandidateOutcome.Hash</c> — that hash keeps its valid-only "publishable" meaning, and
    /// possession of a reviewHash never implies publishable. Two names because they are two
    /// licences: this one says "the user accepted exactly what they saw", the other says "this may
    /// ship".</para>
    /// </summary>
    /// <para>There is no hash without a candidate. A review hash is a licence to accept "exactly what
    /// I saw", and with nothing open there is nothing anyone saw — issuing one would let an accept
    /// arrive for a candidate that had already been discarded and be honoured against the baseline it
    /// happens to match.</para>
    public string ReviewHash()
    {
        Usable();
        if (Candidate is null)
            throw new InvalidOperationException(
                "no candidate is open, so there is nothing to review — a review hash may only be "
                + "issued for a candidate the user can see");

        return DefinitionHash.Of(Lowered());
    }

    /// <summary>The ACCEPTED baseline as canonical per-aggregate files. The candidate never
    /// materializes — files are the projection of what the user approved, nothing else.</summary>
    public CordMaterialized Materialize()
    {
        Usable();
        return CordFiles.Materialize(Accepted);
    }
}
