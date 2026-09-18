// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNet.Emit;

/// <summary>
/// The definition's workflows, compiled into the catalogue the runtime dispatches from.
///
/// <para>Data rather than code, the same as commands and for the same reason: a workflow is a
/// trigger, a condition and a list of effects, and turning it into an imperative method would put
/// one language into two forms inside a single application. What the reader gets instead is a file
/// listing every automatic thing the application does, in one place, in the order the definition
/// says.</para>
///
/// <para><b>An effect this cannot write is reported, never dropped.</b> A workflow whose effects
/// silently disappeared would be an application that looks automated and is not, and nothing in the
/// running system would say so — no error, no log line, just a stamp that never appears.</para>
/// </summary>
public static class WorkflowEmitter
{
    /// <summary>Effect types this emitter writes. Anything else is reported as CORD2303 and the
    /// build refuses without <c>--allow-incomplete</c>.</summary>
    public static readonly IReadOnlySet<string> Emitted =
        new HashSet<string>(StringComparer.Ordinal)
        { "notify", "updateRecord", "createRecord", "createForEach" };

    /// <summary>Trigger events this emitter wires. <c>schedule</c> needs a timer and a host that is
    /// awake, which is a different piece of machinery.</summary>
    public static readonly IReadOnlySet<string> Triggers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "record.created", "record.updated", "field.changed", "schedule",
            "process.state_entered", "command.emitted",
        };

    public static GeneratedFile Workflows(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Conditions;");
        source.Line("using Cordango.Standalone.Workflows;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Workflows;");
        source.Line();
        source.Line("/// <summary>");
        source.Lines(Doc.Summary($"Everything {app.Name} does on its own, without anybody pressing a button.", null));
        source.Line("///");
        source.Line("/// <para>Each entry is a trigger, an optional condition and the effects that follow, in the");
        source.Line("/// order the definition lists them — which is the order they run in. Regenerating replaces");
        source.Line("/// this file; automation of your own belongs in a hook beside it.</para>");
        source.Line("/// </summary>");
        source.Open("public static class AppWorkflows");
        source.Line("public static readonly AppWorkflowCatalogue Catalogue = new(");
        source.Indent();
        source.Line("[");
        source.Indent();

        foreach (var workflow in app.Workflows)
        {
            var trigger = workflow["trigger"] as JsonObject;
            var @event = AppModel.Str(trigger?["event"]);
            if (@event is null || !Triggers.Contains(@event)) continue;

            // A SUBSCRIPTION names an announcement rather than an entity — "when a project is
            // planned", not "when this entity is written". Which entity that turns out to be is
            // resolved here, from the announcement, because the subscriber does not know and must
            // not have to: that is the whole of why one app can react to another.
            var subscription = Subscribed(app, @event, trigger);
            var entity = subscription?.Entity ?? AppModel.Str(trigger?["entity"]);

            if (entity is null) continue;

            // A subscription to an announcement nothing in this build makes. Skipped rather than
            // emitted, and reported as a gap by the generator — a workflow wired to a name no
            // command announces is one that never runs, and looking finished is the worst of it.
            if (IsSubscription(@event) && subscription is null) continue;

            // A schedule nobody can read is a schedule that never fires. Reported rather than
            // emitted, so the build says so instead of the application quietly doing nothing.
            var cron = AppModel.Str(trigger?["cron"]);
            if (@event == "schedule" && !LooksLikeCron(cron)) continue;

            var key = AppModel.Str(workflow["key"]) ?? "workflow";
            var name = AppModel.Str(workflow["name"]) ?? key;
            var field = AppModel.Str(trigger?["field"]);

            var effects = Effects(app, entity, workflow).ToList();
            if (effects.Count == 0) continue;

            source.Line($"new WorkflowDefinition({Naming.Literal(key)}, {Naming.Literal(name)}, "
                + $"{Naming.Literal(entity)}, WorkflowEvent.{EventName(@event)},");
            source.Indent();
            // For a state subscription the field is the lifecycle's, worked out from the
            // announcement rather than written on the trigger.
            var watched = field ?? subscription?.Field;
            source.Line($"Field: {(watched is null ? "null" : Naming.Literal(watched))},");

            ConditionEmitter.TryEmit(workflow["when"], out var when);
            source.Line($"When: {when},");

            if (cron is not null) source.Line($"Cron: {Naming.Literal(cron)},");

            source.Line("Effects:");
            source.Line("[");
            source.Indent();
            foreach (var effect in effects) source.Line(effect + ",");
            source.Outdent();

            // The two subscription facts are init properties, so they land in an object initialiser.
            var extra = new List<string>();
            if (subscription?.State is { } state) extra.Add($"State = {Naming.Literal(state)}");
            if (subscription?.Announcement is { } announced)
                extra.Add($"Announcement = {Naming.Literal(announced)}");

            var initialiser = extra.Count > 0 ? " { " + string.Join(", ", extra) + " }" : "";
            source.Line("])" + initialiser + ",");
            source.Outdent();
        }

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Workflows/AppWorkflows.cs", source.ToString());
    }

    /// <summary>What a subscription resolved to: the entity it really watches, and how.</summary>
    /// <param name="Entity">The ANNOUNCING entity — the one whose record is in scope when it fires.</param>
    /// <param name="Field">The lifecycle's status field, for a state subscription.</param>
    /// <param name="State">The state entered.</param>
    /// <param name="Announcement">The announced name, for a command subscription.</param>
    private sealed record Subscription(
        string Entity, string? Field = null, string? State = null, string? Announcement = null);

    /// <summary>The announcement a subscription names when nothing in this build announces it, or
    /// null when the trigger is fine. For the generator's diagnostic, so that the reason a workflow
    /// will never fire is reported rather than left to be noticed.</summary>
    public static string? Unresolved(AppModel app, JsonObject? trigger)
    {
        ArgumentNullException.ThrowIfNull(app);

        var @event = AppModel.Str(trigger?["event"]);
        if (@event is null || !IsSubscription(@event)) return null;

        // An `app` that SURVIVED the link names an application outside this build — the link strips
        // the qualifier for a sibling precisely because both ends end up in one process. That case is
        // already reported, as the capability it is (CORD2108), and saying it again here in different
        // words would be two entries for one problem.
        if (trigger?["app"] is not null) return null;

        return Subscribed(app, @event, trigger) is null
            ? AppModel.Str(trigger?["name"]) ?? "an unnamed announcement"
            : null;
    }

    private static bool IsSubscription(string @event) =>
        @event is "process.state_entered" or "command.emitted";

    /// <summary>
    /// Turn "when X is announced" into "when THIS entity does THIS".
    ///
    /// <para><b>Both forms name something rather than somewhere, and that asymmetry is the design.</b>
    /// The announcer declares a name and stops; the subscriber names it back. Neither mentions the
    /// other, so an app can be reacted to without knowing it is — which is what makes two apps in a
    /// workspace composable rather than coupled. The cost is that the entity has to be worked out
    /// here, and it is worked out from the merged application, where both apps already are.</para>
    ///
    /// <para><c>process.state_entered</c> announces <c>&lt;entity&gt;.&lt;state&gt;</c>, so the entity
    /// is in the name and the status field comes from that entity's lifecycle. <c>command.emitted</c>
    /// announces whatever the author chose, so the entity is whichever command declares it in
    /// <c>emits</c> — and null when nothing does, which is a subscription to silence.</para>
    /// </summary>
    private static Subscription? Subscribed(AppModel app, string @event, JsonObject? trigger)
    {
        if (AppModel.Str(trigger?["name"]) is not { Length: > 0 } announced) return null;

        if (@event == "command.emitted")
        {
            var announcer = app.Commands.FirstOrDefault(c =>
                AppModel.Arr(c.Json["emits"]).OfType<JsonValue>()
                    .Select(AppModel.Str)
                    .Contains(announced, StringComparer.Ordinal));

            return announcer is null ? null : new Subscription(announcer.Entity, Announcement: announced);
        }

        // '<entity>.<state>'. Split on the FIRST dot: an entity key cannot contain one and a state
        // key cannot either, so anything after the first is not ours to interpret.
        var dot = announced.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == announced.Length - 1) return null;

        var entity = announced[..dot];
        var state = announced[(dot + 1)..];

        // The lifecycle governing that entity is what says which field holds the state. Without one
        // there is no field to watch, and a workflow watching nothing is one that never fires.
        var process = app.Processes.FirstOrDefault(p => p.Entity == entity);
        if (process?.StateField is not { Length: > 0 } field) return null;

        return app.Entity(entity) is null ? null : new Subscription(entity, field, state);
    }

    /// <summary>
    /// Five fields separated by spaces, and nothing more.
    ///
    /// <para>Deliberately shallow. The authority on what a cron expression means is
    /// <c>CronSchedule</c> in the runtime, and this project does not reference the runtime — it
    /// EMBEDS its source, which is the boundary that lets a generated application carry the runtime
    /// without carrying the generator. Duplicating the full parser here to check a build-time
    /// question would be two implementations of a notation, which is exactly the trade that is worth
    /// refusing.</para>
    ///
    /// <para>So this catches the mistake that actually happens — a field missing, or something that
    /// is not cron at all — and the runtime refuses an expression it cannot read, matching nothing
    /// rather than everything.</para>
    /// </summary>
    private static bool LooksLikeCron(string? expression) =>
        expression is { Length: > 0 }
        && expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 5;

    /// <summary>The constant name rather than the string, so a typo in a trigger is a compile error
    /// in the generated application rather than a workflow that never fires.</summary>
    /// <summary>
    /// The constant name rather than the string, so a typo in a trigger is a compile error in the
    /// generated application rather than a workflow that never fires.
    ///
    /// <para>THROWS on an event it does not know, and the throw is the point. This was a switch whose
    /// default was <c>Schedule</c>, so the day two new trigger kinds were added to
    /// <see cref="Triggers"/> they both silently came out as scheduled workflows with no cron — which
    /// is a workflow that never runs, generated without a word. A default that guesses is a default
    /// that hides the next mistake; nothing can reach here that
    /// <see cref="Triggers"/> has not already admitted.</para>
    /// </summary>
    private static string EventName(string @event) => @event switch
    {
        "record.created" => "RecordCreated",
        "record.updated" => "RecordUpdated",
        "field.changed" => "FieldChanged",
        "schedule" => "Schedule",
        "process.state_entered" => "StateEntered",
        "command.emitted" => "CommandEmitted",
        _ => throw new ArgumentOutOfRangeException(nameof(@event), @event,
            "Triggers admits an event this cannot name. Add it here, or it is emitted as something "
            + "else and the workflow never fires."),
    };

    private static IEnumerable<string> Effects(AppModel app, string entity, JsonObject workflow)
    {
        foreach (var effect in AppModel.Arr(workflow["effects"]).OfType<JsonObject>())
        {
            var rendered = Effect(app, entity, effect);
            if (rendered is not null) yield return rendered;
        }
    }

    /// <summary>
    /// One effect as the runtime's own type, or null when this generator cannot write it.
    ///
    /// <para>Public because COMMANDS carry the same effects and must produce the same code. A
    /// command's <c>createRecord</c> written by a second emitter would be a second answer to what
    /// <c>{{record.owner}}</c> means.</para>
    /// </summary>
    public static string? Effect(AppModel app, string entity, JsonObject effect) =>
        Guarded(effect, Constructed(app, entity, effect));

    /// <summary>
    /// The effect's own <c>when</c>, attached to whatever was constructed.
    ///
    /// <para>One place rather than one per effect kind: <c>When</c> is an init property on the base,
    /// so every kind takes it the same way and a kind added later gets it without anybody
    /// remembering to. A guard the emitter dropped would run the effect unconditionally, which is
    /// the failure that looks like the definition rather than like the build.</para>
    /// </summary>
    private static string? Guarded(JsonObject effect, string? constructed)
    {
        if (constructed is null) return null;
        if (effect["when"] is not JsonObject) return constructed;

        return ConditionEmitter.TryEmit(effect["when"], out var when) && when is not null and not "null"
            ? $"{constructed} {{ When = {when} }}"
            : null;
    }

    private static string? Constructed(AppModel app, string entity, JsonObject effect) =>
        AppModel.Str(effect["type"]) switch
        {
            "notify" => $"new NotifyEffect({Naming.Literal(AppModel.Str(effect["to"]) ?? "")}, "
                + $"{Naming.Literal(AppModel.Str(effect["title"]) ?? "")}, "
                + $"{Naming.Literal(AppModel.Str(effect["message"]))}, "
                + $"{Naming.Literal(AppModel.Str(effect["link"]))})",

            "createRecord" when AppModel.Str(effect["entity"]) is { Length: > 0 } target =>
                $"new CreateRecordEffect({Naming.Literal(target)}, [{Sets(effect["set"])}])",

            "updateRecord" => Update(app, entity, effect),

            "createForEach" => ForEach(effect),

            "deleteRecord" => Delete(app, entity, effect),

            _ => null,
        };

    /// <summary>
    /// A grid, laid out once.
    ///
    /// <para>The source is either a date sequence — twelve months from a plan's start — or the rows
    /// of another entity. The <c>key</c> is what makes it once: without it a second save of the same
    /// record lays the grid out again on top of itself, and a plan asked for twelve months ends up
    /// with twenty-four.</para>
    /// </summary>
    private static string? ForEach(JsonObject effect)
    {
        if (AppModel.Str(effect["entity"]) is not { Length: > 0 } target) return null;
        if (effect["source"] is not JsonObject source) return null;

        var rows = source["range"] is JsonObject range
            ? $"new RangeSource({Naming.Literal(AppModel.Str(range["from"]))}, "
                + $"{Naming.Literal(AppModel.Str(range["count"]))}, "
                + $"{Naming.Literal(AppModel.Str(range["step"]) ?? "month")})"
            : AppModel.Str(source["entity"]) is { Length: > 0 } from
                ? $"new EntitySource({Naming.Literal(from)}, [{Filters(source["filters"])}])"
                : null;

        if (rows is null) return null;

        var key = string.Join(", ", AppModel.Arr(effect["key"])
            .Select(k => AppModel.Str(k))
            .Where(k => k is not null)
            .Select(k => Naming.Literal(k!)));

        return $"new CreateForEachEffect({Naming.Literal(target)}, {rows}, [{key}], [{Sets(effect["set"])}])";
    }

    /// <summary>A flat list of field comparisons, ANDed. Deliberately not the condition tree: these
    /// go to the database, and the shapes the corpus uses are all flat.</summary>
    private static string Filters(JsonNode? filters) =>
        string.Join(", ", AppModel.Arr(filters).OfType<JsonObject>()
            .Where(f => AppModel.Str(f["field"]) is { Length: > 0 } && AppModel.Str(f["operator"]) is { Length: > 0 })
            .Select(f => $"new EffectFilter({Naming.Literal(AppModel.Str(f["field"])!)}, "
                + $"{Naming.Literal(AppModel.Str(f["operator"])!)}, "
                + $"{Naming.Literal(Scalar(f["value"]))})"));

    /// <summary>
    /// An update effect, and the one piece of resolution the runtime cannot do for itself.
    ///
    /// <para><c>target: { field: "ticket" }</c> says "write to whatever that reference points at".
    /// At run time all the record holds is a string id, and no amount of looking at it reveals which
    /// TABLE it belongs to. The definition knows — the field is a reference and names its target
    /// entity — so the answer is resolved here, at build time, and carried in the catalogue.</para>
    /// </summary>
    private static string? Update(AppModel app, string entity, JsonObject effect)
    {
        var sets = Sets(effect["set"]);
        if (sets.Length == 0) return null;

        var setIfEmpty = effect["setIfEmpty"]?.GetValue<bool>() ?? false;

        // "self", absent, or an object naming a reference field. Anything else is a shape this does
        // not know, and guessing would write to the wrong record.
        var target = effect["target"];
        var targetField = target is JsonObject reference ? AppModel.Str(reference["field"]) : null;

        if (target is not null && target is not JsonObject && AppModel.Str(target) != "self") return null;

        if (targetField is null)
            return $"new UpdateRecordEffect([{sets}], SetIfEmpty: {Lower(setIfEmpty)})";

        var field = app.Entities.FirstOrDefault(e => e.Key == entity)?.Field(targetField);
        var targetEntity = AppModel.Str(field?.Json["targetEntity"]);
        if (targetEntity is null) return null;

        return $"new UpdateRecordEffect([{sets}], SetIfEmpty: {Lower(setIfEmpty)}, "
            + $"TargetField: {Naming.Literal(targetField)}, TargetEntity: {Naming.Literal(targetEntity)})";
    }

    /// <summary>A delete, resolving its target exactly as <see cref="Update"/> does — same question,
    /// so deliberately not a second answer to it.</summary>
    private static string? Delete(AppModel app, string entity, JsonObject effect)
    {
        var target = effect["target"];
        var targetField = target is JsonObject reference ? AppModel.Str(reference["field"]) : null;

        if (target is not null && target is not JsonObject && AppModel.Str(target) != "self") return null;

        if (targetField is null) return "new DeleteRecordEffect()";

        var field = app.Entities.FirstOrDefault(e => e.Key == entity)?.Field(targetField);
        var targetEntity = AppModel.Str(field?.Json["targetEntity"]);
        if (targetEntity is null) return null;

        return $"new DeleteRecordEffect(TargetField: {Naming.Literal(targetField)}, "
            + $"TargetEntity: {Naming.Literal(targetEntity)})";
    }

    private static string Sets(JsonNode? set) =>
        set is not JsonObject fields ? "" : string.Join(", ", fields.Select(pair => Set(pair.Key, pair.Value)));

    /// <summary>One field an effect writes. Usually a template; sometimes a lookup — "the revenue
    /// plan for this scenario whose tier is flex" — which resolves to that record's id.</summary>
    private static string Set(string field, JsonNode? value) =>
        value is JsonObject { } wrapper && wrapper["pick"] is JsonObject pick
            && AppModel.Str(pick["entity"]) is { Length: > 0 } entity
            ? $"new EffectSet({Naming.Literal(field)}, null, "
                + $"new PickValue({Naming.Literal(entity)}, [{Filters(pick["filters"])}]))"
            : $"new EffectSet({Naming.Literal(field)}, {Naming.Literal(Scalar(value))})";

    private static string Lower(bool value) => value ? "true" : "false";

    private static string? Scalar(JsonNode? value) => value?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}
