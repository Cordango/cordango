// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// What an application DOES: the states a record moves through, the things people can do to it, the
/// work that happens on its own, and who is allowed to do any of it.
///
/// <para><b>The shape decision that defines this slice.</b> In the App Definition a state change is two
/// separate objects that have to agree: a <c>transitions[]</c> entry naming a <c>command</c>, and a
/// <c>commands[]</c> entry carrying the effects. Measured across the corpus, <b>52 of 52</b>
/// transition-linked commands are used by exactly one transition — never shared, never orphaned — and
/// <b>52 of 52</b> carry the same <c>entity</c> as their process. So the split is not a modelling choice
/// anybody made; it is the document's filing system. Cord models the transition as carrying its own
/// effects and DERIVES the command, exactly as <see cref="CordAggregate"/> derives <c>via</c>.</para>
///
/// <para>What that removes is a whole class of valid-but-wrong: a transition naming a command that
/// belongs to another entity, two transitions quietly sharing one command's effects, a command left
/// behind when its transition was deleted. None of them are reachable from Cord, because the author
/// never writes the link.</para>
/// </summary>
/// <param name="StateField">The field holding the state. Every process in the corpus has one; it is not
/// inferable because an entity may carry several selects.</param>
/// <param name="Raw">Nothing today. Present so an unrecognised process property cannot cost totality.</param>
public sealed record CordProcess(
    string? Key = null,
    string? Entity = null,
    string? StateField = null,
    string? InitialState = null,
    IReadOnlyList<CordState>? States = null,
    IReadOnlyList<CordTransition>? Transitions = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<CordState> StateList => States ?? [];
    public IReadOnlyList<CordTransition> TransitionList => Transitions ?? [];

    public static readonly string[] Modelled =
        ["key", "entity", "stateField", "initialState", "states", "transitions"];
}

/// <param name="Terminal">The record is finished. Nothing leaves a terminal state except an explicit
/// reopen.</param>
/// <param name="Phase">Coarse grouping for boards and reporting.</param>
public sealed record CordState(
    string Key,
    string Label,
    string? Color = null,
    bool? Terminal = null,
    string? Phase = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled = ["key", "label", "color", "terminal", "phase"];
}

/// <summary>
/// One move between states, and everything that happens when somebody makes it.
/// </summary>
/// <param name="CommandKey">The key of the command this lowers to. <b>Normally null.</b> It defaults to
/// the transition's own key, which is what a Cord-authored app gets. It is set by the IMPORTER only
/// where a stored document used a different key — 36 of 52 in the corpus did — so the round-trip stays
/// byte-exact without pretending an inference happened. <b>Never on the wire:</b> a command key is a
/// filing detail, and asking an author to invent one is asking them to maintain a join.</param>
/// <param name="Label">Shown on the button. Defaults to the transition's label, which matched in 50 of
/// 52 corpus commands; the two that differed are why this can be overridden at all.</param>
/// <param name="Effects">What actually happens. A transition with none is legitimate — the state change
/// IS the effect — and lowers to a command with an empty effect list.</param>
/// <param name="When">Who may make this move, and when. Lowers to the command's guard, which
/// <c>CommandExecutor</c> evaluates on the SERVER before the state changes — it is enforcement, not
/// button-hiding.
///
/// <para><b>Why this exists at all.</b> Until it did, a transition had no place to put a guard, so
/// every state-moving command in a Cord-authored app was unconditional. The live run of 2026-08-11
/// wrote nine of them and guarded none, and the reason was not that the model was careless: there was
/// no word for it. Eight of those nine were transition-bound. An unmodelled thing is a wall.</para>
///
/// <para>The case it was added for is separation of duties — <c>{ submitted_by, neq,
/// "{{actor.id}}" }</c> on an approval, so the person who filed a claim cannot approve it. A role grant
/// cannot express that: a user who holds both submitter and manager passes the role check on their own
/// record.</para></param>
public sealed record CordTransition(
    string? Key = null,
    string? Label = null,
    IReadOnlyList<string>? From = null,
    string? To = null,
    string? CommandKey = null,
    string? CommandLabel = null,
    string? Icon = null,
    string? Style = null,
    IReadOnlyList<string>? Placements = null,
    string? SuccessMessage = null,
    CordConfirm? Confirm = null,
    CordAsk? Ask = null,
    IReadOnlyList<string>? RequiredFields = null,
    CordWhen? When = null,
    IReadOnlyList<CordEffect>? Effects = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<string> FromList => From ?? [];
    public IReadOnlyList<CordEffect> EffectList => Effects ?? [];

    public static readonly string[] Modelled = ["key", "label", "from", "to", "command", "requiredFields"];
}

/// <summary>
/// Something a person can do to a record that is NOT a state change.
///
/// <para>Eight of the corpus's sixty commands are these. Kept as a separate op from
/// <see cref="CordProcess"/> because they are a different statement: a transition says "this record
/// moves from here to there", an action says "do this to this record". Folding them together would mean
/// a transition with an optional <c>to</c>, and an optional destination is how a state machine stops
/// being one.</para>
/// </summary>
/// <param name="At">Where this sits in the lowered <c>commands[]</c> array. <b>Bookkeeping, never
/// authored.</b> Array order is meaningful to <c>DefinitionHash</c> — it says so at
/// <c>DefinitionHash.Canonical</c> — and a stored document interleaves standalone commands among the
/// transition-derived ones in an order nothing derives: reconstructing "transitions in order, then the
/// rest" reproduced only 10 of 13 corpus apps. Recording the index reproduced <b>13 of 13</b>. Null on
/// anything Cord authored, which simply appends.</param>
public sealed record CordAction(
    string? Key = null,
    string? Label = null,
    string? Entity = null,
    int? At = null,
    string? Description = null,
    string? Icon = null,
    string? Style = null,
    IReadOnlyList<string>? Placements = null,
    string? SuccessMessage = null,
    CordConfirm? Confirm = null,
    CordAsk? Ask = null,
    CordWhen? When = null,
    IReadOnlyList<CordEffect>? Effects = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<CordEffect> EffectList => Effects ?? [];

    public static readonly string[] Modelled =
    [
        "key", "label", "entity", "description", "icon", "style", "placements",
        "successMessage", "confirm", "input", "when", "effects",
    ];
}

/// <param name="Tone">How alarming the dialog looks. <c>danger</c> for anything destructive.</param>
public sealed record CordConfirm(
    string? Title = null,
    string? Message = null,
    string? ConfirmLabel = null,
    string? Tone = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled = ["title", "message", "confirmLabel", "tone"];
}

/// <summary>What the person is asked for before the action runs. The App Definition calls this
/// <c>input</c>; Cord calls it <c>ask</c> because <c>input</c> is already a FIELD property meaning the
/// widget to render, and one word meaning two things in one vocabulary is a trap.</summary>
public sealed record CordAsk(
    IReadOnlyList<string>? Fields = null,
    IReadOnlyList<string>? Required = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled = ["fields", "required"];
}

/// <summary>
/// Work that happens without anybody pressing anything.
/// </summary>
/// <param name="Trigger">What starts it: a record event, or a clock.</param>
public sealed record CordSchedule(
    string? Key = null,
    string? Name = null,
    CordTrigger? Trigger = null,
    CordWhen? When = null,
    IReadOnlyList<CordEffect>? Effects = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<CordEffect> EffectList => Effects ?? [];

    public static readonly string[] Modelled = ["key", "name", "trigger", "when", "effects"];
}

/// <param name="Event">The record event, e.g. <c>record.created</c>, <c>record.updated</c>.</param>
/// <param name="Field">For a field-scoped update event: which field changing counts.</param>
/// <param name="Cron">A clock schedule instead of a record event.</param>
public sealed record CordTrigger(
    string? Event = null,
    string? Entity = null,
    string? Field = null,
    string? Cron = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled = ["event", "entity", "field", "cron"];
}

/// <summary>
/// A condition, either one comparison or all of several.
///
/// <para>Deliberately NOT the expression grammar. <c>ComputedExpr</c> computes a value from a record;
/// this decides whether something runs, it is what the corpus actually uses (14 single comparisons and
/// one <c>all</c>), and giving the author a second place to write <c>a &amp;&amp; b</c> would invite
/// them to write the parts of it the runtime does not evaluate here.</para>
/// </summary>
public sealed record CordWhen(
    string? Field = null,
    string? Operator = null,
    JsonNode? Value = null,
    IReadOnlyList<CordWhen>? All = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled = ["field", "operator", "value", "all"];
}

/// <summary>
/// One thing that happens. Four kinds, which is all the corpus has ever needed.
/// </summary>
/// <param name="Type"><c>updateRecord</c> | <c>createRecord</c> | <c>notify</c> | <c>createForEach</c>.</param>
/// <param name="Target">For <c>updateRecord</c>: <c>self</c>, or a reference field to follow. Absent
/// means self.</param>
/// <param name="SetIfEmpty">Only fill values that are not already set — the difference between "stamp
/// when the first reply happened" and "stamp every reply".</param>
/// <param name="Source">For <c>createForEach</c>: the collection to iterate.</param>
/// <param name="Raw">Effect properties this slice does not model. Every effect type in the corpus is
/// covered, so this is here for what arrives next rather than for what is missing now.</param>
public sealed record CordEffect(
    string? Type = null,
    JsonNode? Target = null,
    JsonObject? Set = null,
    bool? SetIfEmpty = null,
    string? Entity = null,
    string? Source = null,
    string? Key = null,
    string? To = null,
    string? Title = null,
    string? Message = null,
    string? Link = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled =
    [
        "type", "target", "set", "setIfEmpty", "entity", "source", "key",
        "to", "title", "message", "link",
    ];
}

/// <summary>
/// Who may do what.
/// </summary>
/// <param name="Grants">Per entity. A role with no grant for an entity cannot see it at all.</param>
public sealed record CordRole(
    string? Key = null,
    string? Name = null,
    string? Description = null,
    IReadOnlyList<CordGrant>? Grants = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<CordGrant> GrantList => Grants ?? [];

    public static readonly string[] Modelled = ["key", "name", "description", "grants"];
}

/// <param name="Commands">Which commands this role may run. Named in COMMAND keys, which for a
/// transition is the key Cord derived — the one place the derived key surfaces, and the reason
/// <see cref="CordTransition.CommandKey"/> has to be stable rather than regenerated per lowering.</param>
/// <param name="Raw">Today: <c>fieldOverrides</c>, used twice in the corpus.</param>
public sealed record CordGrant(
    string? Entity = null,
    bool? Create = null,
    bool? Read = null,
    bool? Update = null,
    bool? Delete = null,
    IReadOnlyList<string>? Commands = null,
    JsonObject? Raw = null)
{
    /// <summary>The whole application rather than one entity. Named because it is a value with meaning
    /// and not a magic string: every reference app grants an administrator exactly this.</summary>
    public const string EveryEntity = "*";

    public static readonly string[] Modelled =
        ["entity", "create", "read", "update", "delete", "commands"];
}
