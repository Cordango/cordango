// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Notifications;
using Cordango.Standalone.Records;
using Microsoft.Extensions.Logging;

namespace Cordango.Standalone.Workflows;

/// <summary>
/// How deep into workflows-triggering-workflows this request has gone.
///
/// <para>Scoped to the request, because that is the unit a cascade happens in. A workflow writes, the
/// write fires hooks, the hooks run workflows, and one of those writes again. Usually that is the
/// point — stamping a parent is exactly this — and occasionally it is a cycle two rules make between
/// them that neither author can see alone.</para>
/// </summary>
public sealed class WorkflowDepth
{
    /// <summary>Deep enough for a parent, a grandparent and a stamp on each, and shallow enough that
    /// a cycle stops in milliseconds rather than filling the log.</summary>
    public const int Limit = 8;

    public int Value { get; set; }
}

/// <summary>
/// Running the workflows a write triggered.
///
/// <para><b>After the write, always.</b> A condition asks about the record as it now stands, and a
/// notification says what it now is. Running before would mean every rule reasoned about a row that
/// might still fail to save.</para>
///
/// <para><b>Every effect reads the record as the triggering write left it.</b> One snapshot for the
/// whole batch, not re-read between workflows. Workflows are declared independently, often by
/// different people, and two of them watching the same event must not silently depend on which is
/// listed first — with a shared snapshot they cannot. Chaining is still expressible, and more
/// honestly: a workflow's write raises its own event, and the rule that wants the new value watches
/// for THAT rather than relying on where it happens to sit in a list.</para>
///
/// <para>The TARGET of a write is read fresh, which is what makes <c>setIfEmpty</c> mean what it
/// says. Only the record being reacted to is fixed.</para>
///
/// <para><b>A failing effect does not roll back the write.</b> Somebody pressed a button and the
/// record changed; that happened. An effect that cannot run is logged with the workflow's key and
/// skipped, and the remaining effects still run — the alternative is that a full notification table
/// or an unreachable related record silently undoes a person's edit.</para>
/// </summary>
public sealed class WorkflowRunner
{
    private readonly AppWorkflowCatalogue _catalogue;
    private readonly IEnumerable<IEntityWriter> _writers;
    private readonly NotificationService _notifications;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly WorkflowDepth _depth;
    private readonly ILogger<WorkflowRunner> _log;

    public WorkflowRunner(
        AppWorkflowCatalogue catalogue,
        IEnumerable<IEntityWriter> writers,
        NotificationService notifications,
        ICurrentUser user,
        IClock clock,
        WorkflowDepth depth,
        ILogger<WorkflowRunner> log)
    {
        _catalogue = catalogue;
        _writers = writers;
        _notifications = notifications;
        _user = user;
        _clock = clock;
        _depth = depth;
        _log = log;
    }

    /// <summary>A record was inserted.</summary>
    public Task CreatedAsync(string entity, JsonObject record, CancellationToken ct) =>
        RunAsync(entity, record, before: null, ct);

    /// <summary>A record was changed. <paramref name="before"/> is the row as it was, which is the
    /// only way to tell a field that CHANGED from one that was merely written.</summary>
    public Task UpdatedAsync(string entity, JsonObject record, JsonObject before, CancellationToken ct) =>
        RunAsync(entity, record, before, ct);

    private async Task RunAsync(string entity, JsonObject record, JsonObject? before, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var matched = Matching(entity, record, before).ToList();
        if (matched.Count == 0) return;

        if (_depth.Value >= WorkflowDepth.Limit)
        {
            // Named, because "a workflow stopped running" is otherwise indistinguishable from "no
            // workflow matched", and the two need completely different fixes.
            _log.LogWarning(
                "Workflow depth limit ({Limit}) reached on '{Entity}'. Not running: {Workflows}. "
                + "Two rules are most likely writing to each other.",
                WorkflowDepth.Limit, entity, string.Join(", ", matched.Select(w => w.Key)));
            return;
        }

        _depth.Value++;
        try
        {
            foreach (var workflow in matched)
                foreach (var effect in workflow.Effects)
                    await ApplyAsync(workflow, effect, entity, record, ct);
        }
        finally
        {
            _depth.Value--;
        }
    }

    /// <summary>
    /// The workflows this write actually triggered.
    ///
    /// <para>The trigger first, then the condition. A <c>field.changed</c> workflow whose field did
    /// not change is not a workflow whose condition failed — it did not fire at all, and evaluating
    /// its condition would be asking a question nobody posed.</para>
    /// </summary>
    private IEnumerable<WorkflowDefinition> Matching(string entity, JsonObject record, JsonObject? before)
    {
        var created = before is null;

        var triggered = created
            ? _catalogue.For(entity, WorkflowEvent.RecordCreated)
            : _catalogue.For(entity, WorkflowEvent.RecordUpdated)
                .Concat(_catalogue.For(entity, WorkflowEvent.FieldChanged)
                    .Where(w => w.Field is { Length: > 0 } field && Changed(record, before!, field)));

        return triggered.Where(w =>
            ConditionEvaluator.Evaluate(w.When, record, _user.PersonId, _clock.UtcNow));
    }

    /// <summary>
    /// Did this field's VALUE become different?
    ///
    /// <para>Not "was it written". Saving a form resends every field, so a workflow keyed on
    /// <c>field.changed</c> of <c>stage</c> would fire on every save and rewrite a probability
    /// somebody had just corrected by hand. Compared as canonical JSON text, so <c>1</c> and
    /// <c>1.0</c> from two different write paths do not read as a change.</para>
    /// </summary>
    private static bool Changed(JsonObject record, JsonObject before, string field) =>
        !string.Equals(Text(record[field]), Text(before[field]), StringComparison.Ordinal);

    private static string Text(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null ? ""
        : node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>()
        : node.ToJsonString();

    private async Task ApplyAsync(
        WorkflowDefinition workflow, WorkflowEffect effect, string entity, JsonObject record, CancellationToken ct)
    {
        try
        {
            switch (effect)
            {
                case NotifyEffect notify:
                    await _notifications.SendAsync(
                        Fill(notify.To, record),
                        Fill(notify.Title, record) ?? "",
                        Fill(notify.Message, record),
                        notify.Link == "auto" ? $"/record/{entity}/{Id(record)}" : Fill(notify.Link, record),
                        ct);
                    break;

                case CreateRecordEffect create:
                    if (Writer(create.Entity) is not { } inserter)
                    {
                        Missing(workflow, create.Entity);
                        break;
                    }

                    await inserter.CreateAsync(Values(create.Set, record), ct);
                    break;

                case UpdateRecordEffect update:
                    await UpdateAsync(workflow, update, entity, record, ct);
                    break;
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The write already happened and is not being undone. See the class summary.
            _log.LogError(failure,
                "Workflow '{Workflow}' on '{Entity}' could not apply a {Effect}. The record was saved; "
                + "this effect did not run.",
                workflow.Key, entity, effect.GetType().Name);
        }
    }

    private async Task UpdateAsync(
        WorkflowDefinition workflow, UpdateRecordEffect update, string entity, JsonObject record, CancellationToken ct)
    {
        // No target field means the record that triggered this. With one, the write lands on
        // whatever that REFERENCE points at — a message stamping its ticket.
        var targetEntity = update.TargetField is null ? entity : update.TargetEntity;
        var targetId = update.TargetField is null ? Id(record) : Text(record[update.TargetField]);

        if (targetEntity is null || targetId.Length == 0) return;

        if (Writer(targetEntity) is not { } writer)
        {
            Missing(workflow, targetEntity);
            return;
        }

        var target = await writer.FindAsync(targetId, ct);
        if (target is null) return;

        var values = Values(update.Set, record);
        var fields = new List<string>(values.Count);

        foreach (var (field, value) in values)
        {
            // setIfEmpty is what makes "stamp the first reply" mean the first one. Without it the
            // rule would rewrite the stamp on every later message and the field would record the
            // most recent reply under a name that says otherwise.
            if (update.SetIfEmpty && !IsBlank(target[field])) continue;

            // Writing a field the value it already holds would still be a write: it fires hooks,
            // runs workflows, and is how two rules that stamp each other spin. Skipping it makes
            // most cycles impossible rather than merely bounded.
            if (string.Equals(Text(target[field]), Text(value), StringComparison.Ordinal)) continue;

            fields.Add(field);
        }

        if (fields.Count == 0) return;

        await writer.UpdateAsync(targetId, values, fields, ct);
    }

    private IEntityWriter? Writer(string entity) =>
        _writers.FirstOrDefault(w => string.Equals(w.Entity, entity, StringComparison.Ordinal));

    private void Missing(WorkflowDefinition workflow, string entity) =>
        _log.LogError(
            "Workflow '{Workflow}' writes to '{Entity}', which this application has no writer for. "
            + "That should be impossible in a generated application and means the catalogue and the "
            + "registrations disagree.",
            workflow.Key, entity);

    /// <summary>The effect's fields, with every token filled from the record that triggered it.</summary>
    private JsonObject Values(IReadOnlyList<EffectSet> sets, JsonObject record)
    {
        var values = new JsonObject();
        foreach (var set in sets)
            values[set.Field] = Fill(set.Value, record) is { } value ? JsonValue.Create(value) : null;
        return values;
    }

    private string? Fill(string? template, JsonObject record) =>
        ValueTokens.Fill(template, _user.PersonId, _user.UserId, _clock.UtcNow, field => Text(record[field]));

    private static string Id(JsonObject record) => Text(record["id"]);

    private static bool IsBlank(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null
        || (node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Length == 0);
}
