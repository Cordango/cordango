// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;

namespace Cordango.Standalone.Commands;

/// <summary>A field a command writes itself, and the value it writes.</summary>
/// <param name="Field">The field key.</param>
/// <param name="Value">A literal, or one of <c>{{now}}</c>, <c>{{today}}</c>,
/// <c>{{actor.id}}</c>.</param>
public sealed record CommandSet(string Field, string? Value);

/// <summary>
/// Telling somebody that a command ran.
///
/// <para>Every string is a template resolved against the record AFTER the command wrote to it, which
/// is what lets a message say what the record now IS rather than what it was.</para>
/// </summary>
/// <param name="To">Who — usually <c>{{record.submitted_by}}</c>.</param>
/// <param name="Title">The headline.</param>
/// <param name="Message">The body.</param>
/// <param name="Link"><c>auto</c> links to the record the command ran on.</param>
public sealed record CommandNotification(string To, string Title, string? Message = null, string? Link = null);

/// <summary>
/// One thing a person can do to a record beyond editing its fields: approve it, close it, mark it
/// paid.
///
/// <para>Emitted from the definition as compiled data, so the rules a command enforces are the rules
/// the definition states — not a second copy of them written by hand in a controller.</para>
/// </summary>
/// <param name="Key">What the route names.</param>
/// <param name="Label">What the button says.</param>
/// <param name="Entity">The entity it acts on.</param>
/// <param name="StateField">The field the process moves, when this command moves one.</param>
/// <param name="FromStates">The states the record may be in for this to be legal. Empty means the
/// command does not move the record and can run from any state.</param>
/// <param name="ToState">Where the record ends up.</param>
/// <param name="InputFields">Fields the caller supplies.</param>
/// <param name="RequiredInputFields">Which of those may not be left blank.</param>
/// <param name="Sets">Fields the command writes itself.</param>
/// <param name="SuccessMessage">What to tell the person afterwards.</param>
/// <param name="Notifications">Who to tell that it ran.</param>
/// <param name="When">An extra condition the record must satisfy — a guard. Separate from the
/// transition: a transition says which STATES a command may run from, and a guard says everything
/// else. "Do not offboard somebody who already left" is not a state machine, it is one question
/// about one field.</param>
public sealed record CommandDefinition(
    string Key,
    string Label,
    string Entity,
    string? StateField = null,
    IReadOnlyList<string>? FromStates = null,
    string? ToState = null,
    IReadOnlyList<string>? InputFields = null,
    IReadOnlyList<string>? RequiredInputFields = null,
    IReadOnlyList<CommandSet>? Sets = null,
    string? SuccessMessage = null,
    IReadOnlyList<CommandNotification>? Notifications = null,
    Conditions.Condition? When = null)
{
    public IReadOnlyList<CommandNotification> Notifications { get; init; } = Notifications ?? [];

    public IReadOnlyList<string> FromStates { get; init; } = FromStates ?? [];
    public IReadOnlyList<string> InputFields { get; init; } = InputFields ?? [];
    public IReadOnlyList<string> RequiredInputFields { get; init; } = RequiredInputFields ?? [];
    public IReadOnlyList<CommandSet> Sets { get; init; } = Sets ?? [];
}

/// <summary>Every command the application declares, by entity. Generated.</summary>
public sealed class AppCommandCatalogue
{
    public AppCommandCatalogue(IReadOnlyList<CommandDefinition> commands) => Commands = commands;

    public IReadOnlyList<CommandDefinition> Commands { get; }

    public static readonly AppCommandCatalogue Empty = new([]);

    public CommandDefinition? Find(string entity, string key) =>
        Commands.FirstOrDefault(c =>
            string.Equals(c.Entity, entity, StringComparison.Ordinal)
            && string.Equals(c.Key, key, StringComparison.Ordinal));

    public IEnumerable<CommandDefinition> For(string entity) =>
        Commands.Where(c => string.Equals(c.Entity, entity, StringComparison.Ordinal));
}

/// <summary>What running a command produced.</summary>
/// <param name="Record">The record afterwards, projected for the caller's role.</param>
/// <param name="Message">The success message, if the definition wrote one.</param>
public sealed record CommandResult(System.Text.Json.Nodes.JsonObject? Record, string? Message);

/// <summary>
/// Running a command, in the order the checks have to happen.
///
/// <para><b>Permission, then legality, then input, then write.</b> The order is not arbitrary. A
/// caller who may not run the command must be refused before they learn whether the record is in a
/// state that would have allowed it; a record in the wrong state must be refused before its fields
/// are touched; and required input must be checked before anything is saved, because a command that
/// half-ran is worse than one that did not run.</para>
///
/// <para>The write goes through the ordinary store, so a command fires the same hooks an edit does.
/// A workflow watching for a field change does not need to know whether a person typed the value or
/// a command set it.</para>
/// </summary>
public sealed class CommandService<T> where T : class, IRecord, new()
{
    private readonly IRecordStore<T> _store;
    private readonly AppCommandCatalogue _catalogue;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly Notifications.NotificationService _notifications;

    public CommandService(
        IRecordStore<T> store,
        AppCommandCatalogue catalogue,
        ICurrentUser user,
        IClock clock,
        Notifications.NotificationService notifications)
    {
        _store = store;
        _catalogue = catalogue;
        _user = user;
        _clock = clock;
        _notifications = notifications;
    }

    private static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{\{([^}]+)\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<CommandResult> RunAsync(
        string id, string commandKey, JsonElement input, EntityAccess access, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var entity = _store.Descriptor.EntityKey;

        var command = _catalogue.Find(entity, commandKey)
            ?? throw new RecordException("command.unknown", $"'{entity}' has no command '{commandKey}'.", 404);

        // Deny-by-default, and checked first. Telling somebody the record is in the wrong state
        // before telling them they may not do this at all leaks the record's state to a caller who
        // is not allowed to ask.
        if (!access.CanRunCommand(commandKey))
            throw RecordException.Forbidden("command.denied", $"Your role may not run '{command.Label}'.");

        var record = await _store.FindAsync(id, ct)
            ?? throw RecordException.NotFound(entity, id);

        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (command.StateField is { } stateField && command.FromStates.Count > 0)
        {
            var current = Read(record, stateField);
            if (!command.FromStates.Contains(current, StringComparer.Ordinal))
                throw new RecordException("command.illegal_transition",
                    $"'{command.Label}' cannot run on a record that is '{current}'.", 409);
        }

        // The guard, after the transition and before anything is read from the caller.
        //
        // After the transition, because "this claim is already reimbursed" is a better answer than
        // "this claim does not qualify" and the state is the more specific fact. Before the input,
        // because a command that will not run should not be telling the caller which fields it would
        // have wanted.
        //
        // The message stays general on purpose. A guard may read a field the caller's role cannot,
        // and naming it in a refusal would be a way to read it.
        if (command.When is { } guard
            && !ConditionEvaluator.Evaluate(guard, Snapshot(record), _user.PersonId, _clock.UtcNow))
            throw new RecordException("command.not_applicable",
                $"'{command.Label}' cannot run on this record.", 409);

        foreach (var field in command.InputFields)
        {
            var supplied = input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var value)
                ? Text(value)
                : null;

            if (command.RequiredInputFields.Contains(field, StringComparer.Ordinal)
                && string.IsNullOrWhiteSpace(supplied))
                throw new RecordException("command.input_required",
                    $"'{command.Label}' needs {field}.", 400, [field]);

            if (supplied is not null) changes[field] = supplied;
        }

        foreach (var set in command.Sets) changes[set.Field] = Resolve(set.Value);

        // The state move is applied LAST, so a set or an input naming the state field cannot
        // override where the process says the record goes.
        if (command.StateField is { } field2 && command.ToState is { } target) changes[field2] = target;

        foreach (var key in changes.Keys)
            if (!_store.Descriptor.TryGetField(key, out _))
                throw new RecordException("command.field_unknown",
                    $"'{command.Label}' writes '{key}', which '{entity}' does not have.");

        // Built as JSON and deserialised once, so a value converts exactly as it would have on an
        // ordinary PATCH of the same field. Two conversion paths for one field is how a command
        // ends up storing "2026-01-02T00:00:00" where an edit stores "2026-01-02".
        var patch = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in changes)
            patch[key] = value is null ? null : System.Text.Json.Nodes.JsonValue.Create(value);

        var incoming = patch.Deserialize<T>(Json) ?? new T();
        incoming.Id = record.Id;

        var updated = await _store.UpdateAsync(id, incoming, [.. changes.Keys], ct);

        // After the write, so a message can say what the record now is — and so that a failure to
        // notify cannot roll back a decision somebody already made.
        await NotifyAsync(command, updated, ct);

        return new CommandResult(
            RecordVisibility.Project(access, updated, Json),
            command.SuccessMessage);
    }

    private async Task NotifyAsync(CommandDefinition command, T record, CancellationToken ct)
    {
        if (command.Notifications.Count == 0) return;

        var node = JsonSerializer.SerializeToNode(record, Json)!.AsObject();

        foreach (var notification in command.Notifications)
        {
            var link = notification.Link == "auto"
                ? $"/record/{_store.Descriptor.EntityKey}/{record.Id}"
                : Render(notification.Link, node);

            await _notifications.SendAsync(
                Render(notification.To, node),
                Render(notification.Title, node) ?? "",
                Render(notification.Message, node),
                link,
                ct);
        }
    }

    /// <summary>
    /// Fill <c>{{record.field}}</c> and the actor tokens from the record as it now stands.
    ///
    /// <para>Rendered from the FULL record, not from a caller-projected copy — a notification goes to
    /// a configured recipient rather than back down the wire to whoever pressed the button, so it may
    /// legitimately quote a field the caller could not read.</para>
    /// </summary>
    private string? Render(string? template, System.Text.Json.Nodes.JsonObject record)
    {
        if (template is null) return null;

        return Placeholder.Replace(template, match =>
        {
            var token = match.Groups[1].Value.Trim();

            if (token.StartsWith("record.", StringComparison.Ordinal))
                return record[token["record.".Length..]]?.ToString() ?? "";

            return token switch
            {
                "actor.id" => _user.PersonId ?? "",
                "now" => _clock.UtcNow.ToString("O"),
                "today" => DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).ToString("yyyy-MM-dd"),
                _ => match.Value,
            };
        });
    }

    /// <summary>Placeholders a definition may write in a command's <c>set</c>.</summary>
    private string? Resolve(string? value) => value switch
    {
        null => null,
        "{{now}}" => _clock.UtcNow.ToString("O"),
        "{{today}}" => DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).ToString("yyyy-MM-dd"),
        "{{actor.id}}" => _user.PersonId,
        "{{actor.userId}}" => _user.UserId,
        _ => value,
    };

    /// <summary>One field's current value, as text. Used only to test the record's state against
    /// the transition, which is a string comparison in the definition too.</summary>
    private static string Read(T record, string field)
    {
        var node = JsonSerializer.SerializeToNode(record, Json)?.AsObject();
        return node?[field]?.ToString() ?? "";
    }

    /// <summary>The whole record as JSON, which is what a condition reads.
    ///
    /// <para>The FULL record, not the caller's projection. A guard is the application's rule, not the
    /// caller's — one that reads a field this role cannot see must still get the true answer, or the
    /// same command would be legal for one person and not another for reasons the definition never
    /// stated.</para></summary>
    private static System.Text.Json.Nodes.JsonObject Snapshot(T record) =>
        JsonSerializer.SerializeToNode(record, Json)?.AsObject() ?? [];

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    /// <summary>Numbers are read from strings as well as from JSON numbers: a command's <c>set</c>
    /// is text in the definition, and refusing to parse "0" into a decimal would make half the
    /// language unusable from here.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}
