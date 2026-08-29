// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
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

    /// <summary>
    /// The grid rows laid out so far in this request, as <c>entity + key</c>.
    ///
    /// <para><b>Shared across nesting, which is the whole reason it lives here.</b> A
    /// <c>createForEach</c> reads the rows that already exist once, before its loop — one query
    /// rather than one per row. But creating a row fires that row's own workflows, and if one of
    /// THOSE lays out the same grid it starts from its own snapshot, creates the rows the outer loop
    /// has not reached yet, and then the outer loop creates them a second time. A plan asked for
    /// three months ends up with six.</para>
    ///
    /// <para>Keeping the set on the request rather than on the loop makes every layer of that
    /// nesting see the same "already done" list, so the key means what it says however the calls
    /// interleave.</para>
    /// </summary>
    public HashSet<string> LaidOut { get; } = new(StringComparer.Ordinal);
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

    /// <summary>
    /// One record, one scheduled workflow, because the clock said so.
    ///
    /// <para>Its own entry point rather than a fake write: there is no trigger to match — the
    /// scheduler has already decided this minute is one of the workflow's — and no <c>before</c> to
    /// compare against. Everything after the condition is the ordinary path, so a scheduled
    /// workflow's effects, cycle guard and logging are the ones every other workflow gets.</para>
    /// </summary>
    public async Task ScheduledAsync(WorkflowDefinition workflow, JsonObject record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(record);

        if (!ConditionEvaluator.Evaluate(workflow.When, record, _user.PersonId, _clock.UtcNow)) return;

        _depth.Value++;
        try
        {
            JsonObject? created = null;
            foreach (var effect in workflow.Effects)
                created = await ApplyAsync(
                    $"Workflow '{workflow.Key}'", effect, workflow.Entity, record, created, ct) ?? created;
        }
        finally
        {
            _depth.Value--;
        }
    }

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
            {
                // Per workflow, not per batch: two workflows watching the same event are declared
                // independently, and letting one see what the other created would make the order they
                // happen to be listed in part of what they mean.
                JsonObject? created = null;
                foreach (var effect in workflow.Effects)
                    created = await ApplyAsync(
                        $"Workflow '{workflow.Key}'", effect, entity, record, created, ct) ?? created;
            }
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

    /// <summary>
    /// Effects belonging to something that is not a workflow — a command.
    ///
    /// <para>A command's <c>createRecord</c> and a workflow's are the same effect and must behave
    /// the same way: the same token filling, the same depth guard against two rules that stamp each
    /// other, the same decision not to undo the write when an effect fails. A second implementation
    /// on the command side would be a second set of answers to all of that, so commands run through
    /// this one.</para>
    /// </summary>
    /// <param name="source">What to name in the log if an effect cannot run — "Command 'approve'".</param>
    public async Task RunEffectsAsync(
        IReadOnlyList<WorkflowEffect> effects, string source, string entity, JsonObject record,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(effects);

        // What the most recent createRecord inserted, so a later effect can point at it with
        // {{created.id}}. Carried through the loop rather than held on this runner: a workflow can
        // trigger another one, and a field would let the inner run's creation overwrite the outer's.
        JsonObject? created = null;

        foreach (var effect in effects)
            created = await ApplyAsync(source, effect, entity, record, created, ct) ?? created;
    }

    /// <summary>Runs one effect and returns the record it inserted, or null when it inserted
    /// nothing — which is what lets the next effect in the list name it.</summary>
    private async Task<JsonObject?> ApplyAsync(
        string source, WorkflowEffect effect, string entity, JsonObject record,
        JsonObject? created, CancellationToken ct)
    {
        // This effect's own condition, on the record as it is NOW. Checked here rather than where
        // the list is built, so it sees the record after any earlier effect in the same list wrote
        // to it — "email them only if the status ended up rejected" reads the status the effect
        // above just set.
        if (!ConditionEvaluator.Evaluate(effect.When, record, _user.PersonId, _clock.UtcNow)) return null;

        try
        {
            switch (effect)
            {
                case NotifyEffect notify:
                    await _notifications.SendAsync(
                        Fill(notify.To, record, null, created),
                        Fill(notify.Title, record, null, created) ?? "",
                        Fill(notify.Message, record, null, created),
                        notify.Link == "auto"
                            ? $"/record/{entity}/{Id(record)}"
                            : Fill(notify.Link, record, null, created),
                        ct);
                    break;

                case CreateRecordEffect create:
                    if (Writer(create.Entity) is not { } inserter)
                    {
                        Missing(source, create.Entity);
                        break;
                    }

                    // Returned rather than discarded: this is the only moment the new record's id
                    // exists, and {{created.id}} on a later effect is the only way to name it.
                    return await inserter.CreateAsync(
                        await ValuesAsync(create.Set, record, null, created, ct), ct);

                case UpdateRecordEffect update:
                    await UpdateAsync(source, update, entity, record, created, ct);
                    break;

                case CreateForEachEffect forEach:
                    await ForEachAsync(source, forEach, record, created, ct);
                    break;

                case DeleteRecordEffect delete:
                    await DeleteAsync(source, delete, entity, record, ct);
                    break;
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The write already happened and is not being undone. See the class summary.
            _log.LogError(failure,
                "{Source} on '{Entity}' could not apply a {Effect}. The record was saved; "
                + "this effect did not run.",
                source, entity, effect.GetType().Name);
        }

        return null;
    }

    /// <summary>
    /// Remove the triggering record, or whatever a reference on it points at.
    ///
    /// <para>The same target resolution an update does, because "which record" is the same question
    /// and two answers to it would eventually disagree. What differs is that there is nothing to
    /// compare first: an update skips a write that would change nothing, and a delete of a record
    /// that is already gone is handled inside the writer rather than here.</para>
    /// </summary>
    private async Task DeleteAsync(
        string source, DeleteRecordEffect delete, string entity, JsonObject record, CancellationToken ct)
    {
        var targetEntity = delete.TargetField is null ? entity : delete.TargetEntity;
        var targetId = delete.TargetField is null ? Id(record) : Text(record[delete.TargetField]);

        if (targetEntity is null || targetId.Length == 0) return;

        if (Writer(targetEntity) is not { } writer)
        {
            Missing(source, targetEntity);
            return;
        }

        await writer.DeleteAsync(targetId, ct);
    }

    private async Task UpdateAsync(
        string source, UpdateRecordEffect update, string entity, JsonObject record,
        JsonObject? created, CancellationToken ct)
    {
        // No target field means the record that triggered this. With one, the write lands on
        // whatever that REFERENCE points at — a message stamping its ticket.
        var targetEntity = update.TargetField is null ? entity : update.TargetEntity;
        var targetId = update.TargetField is null ? Id(record) : Text(record[update.TargetField]);

        if (targetEntity is null || targetId.Length == 0) return;

        if (Writer(targetEntity) is not { } writer)
        {
            Missing(source, targetEntity);
            return;
        }

        var target = await writer.FindAsync(targetId, ct);
        if (target is null) return;

        var values = Values(update.Set, record, created);
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

    /// <summary>
    /// One created record per source row, skipping the ones already there.
    ///
    /// <para>This is how a plan lays out its months and how a grid crosses a segment with each of its
    /// lifecycle steps. Two things make it safe to run more than once: the key, which identifies a
    /// row that already exists, and the fact that existing rows are read ONCE before the loop rather
    /// than asked about per row.</para>
    /// </summary>
    private async Task ForEachAsync(
        string source, CreateForEachEffect effect, JsonObject record, JsonObject? created,
        CancellationToken ct)
    {
        if (Writer(effect.Entity) is not { } writer)
        {
            Missing(source, effect.Entity);
            return;
        }

        var rows = await SourceRowsAsync(source, effect.Source, record, ct);
        if (rows.Count == 0) return;

        // Every row that already exists, in one query, keyed by the fields the definition says
        // identify it. Asking per row would be one round trip per month of a plan.
        //
        // Merged into the REQUEST's set rather than kept local: see WorkflowDepth.LaidOut for why a
        // local one lays some rows out twice.
        if (effect.Key.Count > 0)
            foreach (var row in await writer.WhereAsync([], ct))
                _depth.LaidOut.Add(effect.Entity + '' + KeyOf(row, effect.Key));

        foreach (var row in rows)
        {
            var values = await ValuesAsync(effect.Set, record, row, created, ct);

            if (effect.Key.Count > 0
                && !_depth.LaidOut.Add(effect.Entity + '' + KeyOf(values, effect.Key)))
                continue;

            await writer.CreateAsync(values, ct);
        }
    }

    /// <summary>
    /// A key that identifies one row of a grid: the named fields, in the order the definition lists
    /// them, joined by a separator no id contains.
    /// </summary>
    private static string KeyOf(JsonObject record, IReadOnlyList<string> fields) =>
        string.Join('', fields.Select(f => Text(record[f])));

    private async Task<IReadOnlyList<JsonObject>> SourceRowsAsync(
        string origin, ForEachSource source, JsonObject record, CancellationToken ct)
    {
        switch (source)
        {
            case EntitySource entity:
                if (Writer(entity.Entity) is not { } reader)
                {
                    Missing(origin, entity.Entity);
                    return [];
                }

                return await reader.WhereAsync(Filters(entity.Filters, record), ct);

            case RangeSource range:
                return Dates(range, record);

            default:
                return [];
        }
    }

    /// <summary>
    /// A sequence of dates as source rows.
    ///
    /// <para>Each row carries <c>index</c> (1-based, because a person counting months starts at
    /// one), <c>date</c> and <c>end</c> — the last day before the next step begins, which is what
    /// makes a month row cover a whole month rather than a single day.</para>
    /// </summary>
    private static IReadOnlyList<JsonObject> Dates(RangeSource range, JsonObject record)
    {
        var from = ValueTokens.Fill(range.From, null, null, default, field => Text(record[field]));
        var countText = ValueTokens.Fill(range.Count, null, null, default, field => Text(record[field]));

        if (!DateOnly.TryParse(from, CultureInfo.InvariantCulture, out var start)) return [];
        if (!int.TryParse(countText, CultureInfo.InvariantCulture, out var count)) return [];

        // A guard rather than a configuration knob. A definition asking for a million rows is a
        // mistake, and the honest failure is a short grid somebody notices rather than a request
        // that never returns.
        count = Math.Clamp(count, 0, 1000);

        var rows = new List<JsonObject>(count);
        for (var i = 0; i < count; i++)
        {
            var at = Advance(start, range.Step, i);
            var next = Advance(start, range.Step, i + 1);

            rows.Add(new JsonObject
            {
                ["index"] = i + 1,
                ["date"] = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["end"] = next.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        }

        return rows;
    }

    private static DateOnly Advance(DateOnly start, string step, int times) => step switch
    {
        "day" => start.AddDays(times),
        "week" => start.AddDays(7 * times),
        "year" => start.AddYears(times),
        _ => start.AddMonths(times),
    };

    /// <summary>The effect's fields, filled from the triggering record and — for a
    /// <c>createForEach</c> — the source row, with any looked-up values resolved.</summary>
    private async Task<JsonObject> ValuesAsync(
        IReadOnlyList<EffectSet> sets, JsonObject record, JsonObject? source, JsonObject? created,
        CancellationToken ct)
    {
        var values = new JsonObject();

        foreach (var set in sets)
        {
            if (set.Pick is { } pick)
            {
                values[set.Field] = await PickAsync(pick, record, source, created, ct) is { } id ? JsonValue.Create(id) : null;
                continue;
            }

            var filled = Fill(set.Value, record, source, created);
            values[set.Field] = filled is null ? null : JsonValue.Create(filled);
        }

        return values;
    }

    /// <summary>One looked-up id, or null when nothing matches — a reference to a row that does not
    /// exist is worse than a blank.</summary>
    private async Task<string?> PickAsync(PickValue pick, JsonObject record, JsonObject? source,
        JsonObject? created, CancellationToken ct)
    {
        if (Writer(pick.Entity) is not { } reader) return null;

        var matches = await reader.WhereAsync(Filters(pick.Filters, record, source, created), ct);
        return matches.Count == 0 ? null : Text(matches[0]["id"]);
    }

    private IReadOnlyList<RecordFilter> Filters(
        IReadOnlyList<EffectFilter> filters, JsonObject record, JsonObject? source = null,
        JsonObject? created = null) =>
        [.. filters.Select(f => new RecordFilter(f.Field, f.Operator, Fill(f.Value, record, source, created)))];

    private IEntityWriter? Writer(string entity) =>
        _writers.FirstOrDefault(w => string.Equals(w.Entity, entity, StringComparison.Ordinal));

    private void Missing(string source, string entity) =>
        _log.LogError(
            "{Source} writes to '{Entity}', which this application has no writer for. "
            + "That should be impossible in a generated application and means the catalogue and the "
            + "registrations disagree.",
            source, entity);

    /// <summary>The effect's fields, with every token filled from the record that triggered it.</summary>
    private JsonObject Values(IReadOnlyList<EffectSet> sets, JsonObject record, JsonObject? created = null)
    {
        var values = new JsonObject();
        foreach (var set in sets)
            values[set.Field] = Fill(set.Value, record, null, created) is { } value ? JsonValue.Create(value) : null;
        return values;
    }

    private string? Fill(string? template, JsonObject record, JsonObject? source = null,
        JsonObject? created = null) =>
        ValueTokens.Fill(template, _user.PersonId, _user.UserId, _clock.UtcNow,
            field => Text(record[field]),
            source is null ? null : field => Text(source[field]),
            created is null ? null : field => Text(created[field]));

    private static string Id(JsonObject record) => Text(record["id"]);

    private static bool IsBlank(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null
        || (node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Length == 0);
}
