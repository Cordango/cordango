// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Conditions;

namespace Cordango.Standalone.Workflows;

/// <summary>What makes a workflow run.</summary>
public static class WorkflowEvent
{
    /// <summary>A record of this entity was inserted.</summary>
    public const string RecordCreated = "record.created";

    /// <summary>A record of this entity was changed, whatever changed.</summary>
    public const string RecordUpdated = "record.updated";

    /// <summary>One named field's value became different from what it was. Not "was written" —
    /// saving a form without touching the field must not fire it, or a stage-probability rule
    /// rewrites the probability somebody just corrected by hand.</summary>
    public const string FieldChanged = "field.changed";

    /// <summary>On a clock rather than on a write.</summary>
    public const string Schedule = "schedule";

    /// <summary>
    /// A record entered a state of its lifecycle, whichever transition took it there.
    ///
    /// <para>Deliberately the STATE and not the transition. "When a request becomes approved" is the
    /// thing anybody means; naming a transition makes the rule miss the day somebody adds a second
    /// way to reach the same state — an escalation path, a bulk approve — and nothing reports it.</para>
    /// </summary>
    public const string StateEntered = "process.state_entered";

    /// <summary>
    /// A command announced something, and this workflow subscribes to the announcement.
    ///
    /// <para>The announcer knows nothing about the subscriber: a command declares what it ANNOUNCES
    /// and that is the whole of its side of the contract. That is what lets one app react to another
    /// without either one importing the other — and in a merged workspace the two are simply in the
    /// same process.</para>
    /// </summary>
    public const string CommandEmitted = "command.emitted";
}

/// <summary>
/// Something a workflow does once its trigger fired and its condition held.
/// </summary>
public abstract record WorkflowEffect
{
    /// <summary>
    /// A condition on THIS effect, evaluated after the workflow's own and before the effect runs.
    /// Null means run it.
    ///
    /// <para><b>Why a second condition.</b> A workflow's <c>when</c> decides whether the workflow
    /// fires at all, and everything in its list then runs together. But the things that should happen
    /// when a record changes are rarely all governed by one rule — approving a request should stamp
    /// the approval date always, and email the requester only when they asked to be told. Without
    /// this the author's only move is to split one workflow into several, each with a nearly
    /// identical trigger, which multiplies the triggers a reader has to hold in their head and makes
    /// the ORDER the effects run in an accident of how the workflows happened to be listed.</para>
    ///
    /// <para>An init property on the base rather than a constructor parameter on each subtype, so
    /// adding it costs no positional argument at any of the existing call sites and an effect that
    /// does not use it reads exactly as it did.</para>
    /// </summary>
    public Condition? When { get; init; }
}

/// <summary>
/// Write fields onto a record.
///
/// <para><paramref name="TargetField"/> is null for the record that triggered the workflow, and
/// otherwise names a REFERENCE field on it — a message stamping its ticket writes
/// <c>target: { field: "ticket" }</c>, and the write lands on whatever that reference points
/// at.</para>
/// </summary>
/// <param name="Set">The fields to write, values possibly carrying <c>{{record.x}}</c> or
/// <c>{{actor.id}}</c>.</param>
/// <param name="SetIfEmpty">Write only where the target's field is currently blank. This is what
/// makes "stamp the first reply time" mean the FIRST one.</param>
/// <param name="TargetEntity">Which entity the reference points at, when it points somewhere else.
/// The definition knows it at build time and the runtime cannot work it out from a string id.</param>
public sealed record UpdateRecordEffect(
    IReadOnlyList<EffectSet> Set,
    bool SetIfEmpty = false,
    string? TargetField = null,
    string? TargetEntity = null) : WorkflowEffect;

/// <summary>Insert a record of another entity — a scenario being created seeding the plan rows a
/// person will then edit.</summary>
public sealed record CreateRecordEffect(string Entity, IReadOnlyList<EffectSet> Set) : WorkflowEffect;

/// <summary>
/// Remove a record: the triggering one, or whatever a reference on it points at.
///
/// <para><b>The delete is real and nothing undoes it.</b> Every other effect writes something that
/// can be corrected afterwards by editing the row; this one leaves nothing to edit. It runs through
/// the ordinary store, so the entity's own delete hooks fire and anything cascading from them
/// happens too — a workflow deleting a parent is a workflow deleting its children.</para>
///
/// <para>Which is why the gate refuses an UNCONDITIONAL self-delete on a write trigger: a
/// <c>record.created</c> workflow that deletes <c>self</c> with no <see cref="WorkflowEffect.When"/>
/// removes every record of that entity as fast as anybody can make one, and the symptom is a table
/// that stays empty while the application reports every save as successful.</para>
/// </summary>
/// <param name="TargetField">Null for the triggering record; otherwise a reference field on it.</param>
/// <param name="TargetEntity">What that reference points at, resolved at build time — a string id
/// does not say which table it belongs to.</param>
public sealed record DeleteRecordEffect(
    string? TargetField = null,
    string? TargetEntity = null) : WorkflowEffect;

/// <summary>Tell somebody. The same in-app notification a command raises.</summary>
public sealed record NotifyEffect(string To, string Title, string? Message = null, string? Link = null)
    : WorkflowEffect;

/// <summary>Where the rows of a <see cref="CreateForEachEffect"/> come from.</summary>
public abstract record ForEachSource;

/// <summary>
/// A sequence of dates: twelve months from a plan's start.
///
/// <para><paramref name="From"/> and <paramref name="Count"/> are templates rather than values,
/// because both come off the record that triggered the workflow — a scenario knows its own start
/// date and how many months it plans.</para>
/// </summary>
/// <param name="Step">day, week, month or year.</param>
public sealed record RangeSource(string From, string Count, string Step) : ForEachSource;

/// <summary>Every record of another entity that matches, one created record per source row. This is
/// how a grid is laid out: a segment crossed with each adoption point.</summary>
public sealed record EntitySource(string Entity, IReadOnlyList<EffectFilter> Filters) : ForEachSource;

/// <summary>One field comparison in a source or pick filter, ANDed with the rest. Values may carry
/// the same tokens an effect's <c>set</c> does.</summary>
public sealed record EffectFilter(string Field, string Operator, string? Value);

/// <summary>
/// Create one record per source row, once.
///
/// <para><b><paramref name="Key"/> is what makes it once.</b> The fields named there identify a row
/// uniquely, and a row that already exists is skipped. Without it, re-running the workflow — which a
/// second save of the same record does — would lay the grid out again on top of itself, and the
/// symptom is a plan with twenty-four months where twelve were asked for.</para>
/// </summary>
public sealed record CreateForEachEffect(
    string Entity,
    ForEachSource Source,
    IReadOnlyList<string> Key,
    IReadOnlyList<EffectSet> Set) : WorkflowEffect;

/// <summary>
/// A value looked up rather than written: "the revenue plan for this scenario whose tier is flex".
///
/// <para>Resolves to that record's id, or to nothing when no record matches — a reference to a row
/// that does not exist is worse than a blank.</para>
/// </summary>
public sealed record PickValue(string Entity, IReadOnlyList<EffectFilter> Filters);

/// <summary>One field an effect writes, and the value it writes. The value may be a literal or carry
/// <c>{{record.field}}</c>, <c>{{source.field}}</c>, <c>{{actor.id}}</c>, <c>{{today}}</c> and the
/// date offsets — or, instead of a value, a <paramref name="Pick"/> that looks one up.</summary>
public sealed record EffectSet(string Field, string? Value, PickValue? Pick = null);

/// <summary>
/// One workflow: when it runs, whether it should, and what it does.
///
/// <para>Compiled data rather than generated code, the same as a command. A workflow is a small
/// declarative thing — a trigger, a condition, a list of effects — and turning it into an imperative
/// method would put the same language into two forms inside one application: workflows as C#,
/// filters and guards still as data. One shape, one evaluator.</para>
/// </summary>
/// <param name="Key">What the definition calls it, used in logs and errors.</param>
/// <param name="Name">What a person calls it.</param>
/// <param name="Entity">The entity whose writes it watches.</param>
/// <param name="Event">One of <see cref="WorkflowEvent"/>.</param>
/// <param name="Field">Which field, for <c>field.changed</c>.</param>
/// <param name="When">An optional condition on the record, evaluated AFTER the trigger matches.</param>
/// <param name="Effects">What to do, in the order the definition lists them.</param>
/// <param name="Cron">Five-field cron, for <c>schedule</c>. In UTC — a generated application has no
/// timezone at the scheduler level, and "8am" meaning two different things in June and December is a
/// surprise nobody asked for.</param>
public sealed record WorkflowDefinition(
    string Key,
    string Name,
    string Entity,
    string Event,
    string? Field = null,
    Condition? When = null,
    IReadOnlyList<WorkflowEffect>? Effects = null,
    string? Cron = null)
{
    public IReadOnlyList<WorkflowEffect> Effects { get; init; } = Effects ?? [];

    /// <summary>
    /// For <see cref="WorkflowEvent.StateEntered"/>: the state the record has to have entered.
    ///
    /// <para>Paired with <see cref="Field"/>, which carries the lifecycle's own status field —
    /// the same pair <c>field.changed</c> uses, because "entered a state" IS that field becoming this
    /// value. Reusing them rather than adding a second mechanism means the answer to "did it really
    /// change, or was the form just saved again" is the one already written down.</para>
    /// </summary>
    public string? State { get; init; }

    /// <summary>For <see cref="WorkflowEvent.CommandEmitted"/>: the announced name subscribed to,
    /// as a command's <c>emits</c> spells it.</summary>
    public string? Announcement { get; init; }
}

/// <summary>Every workflow the application declares. Generated.</summary>
public sealed class AppWorkflowCatalogue
{
    public AppWorkflowCatalogue(IReadOnlyList<WorkflowDefinition> workflows) => Workflows = workflows;

    public IReadOnlyList<WorkflowDefinition> Workflows { get; }

    public static readonly AppWorkflowCatalogue Empty = new([]);

    /// <summary>The workflows watching this entity for this event, in declaration order — which is
    /// the order they run in, and the only order a person reading the definition could predict.</summary>
    public IEnumerable<WorkflowDefinition> For(string entity, string @event) =>
        Workflows.Where(w =>
            string.Equals(w.Entity, entity, StringComparison.Ordinal)
            && string.Equals(w.Event, @event, StringComparison.Ordinal));
}
