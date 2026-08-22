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
}

/// <summary>Something a workflow does once its trigger fired and its condition held.</summary>
public abstract record WorkflowEffect;

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

/// <summary>Tell somebody. The same in-app notification a command raises.</summary>
public sealed record NotifyEffect(string To, string Title, string? Message = null, string? Link = null)
    : WorkflowEffect;

/// <summary>One field an effect writes, and the value it writes. The value may be a literal or carry
/// <c>{{record.field}}</c>, <c>{{actor.id}}</c>, <c>{{today}}</c> and the date offsets.</summary>
public sealed record EffectSet(string Field, string? Value);

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
public sealed record WorkflowDefinition(
    string Key,
    string Name,
    string Entity,
    string Event,
    string? Field = null,
    Condition? When = null,
    IReadOnlyList<WorkflowEffect>? Effects = null)
{
    public IReadOnlyList<WorkflowEffect> Effects { get; init; } = Effects ?? [];
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
