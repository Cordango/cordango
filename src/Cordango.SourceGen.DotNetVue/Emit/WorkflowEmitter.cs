// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

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
        new HashSet<string>(StringComparer.Ordinal) { "notify", "updateRecord", "createRecord" };

    /// <summary>Trigger events this emitter wires. <c>schedule</c> needs a timer and a host that is
    /// awake, which is a different piece of machinery.</summary>
    public static readonly IReadOnlySet<string> Triggers =
        new HashSet<string>(StringComparer.Ordinal) { "record.created", "record.updated", "field.changed" };

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
            var entity = AppModel.Str(trigger?["entity"]);

            if (@event is null || entity is null || !Triggers.Contains(@event)) continue;

            var key = AppModel.Str(workflow["key"]) ?? "workflow";
            var name = AppModel.Str(workflow["name"]) ?? key;
            var field = AppModel.Str(trigger?["field"]);

            var effects = Effects(app, entity, workflow).ToList();
            if (effects.Count == 0) continue;

            source.Line($"new WorkflowDefinition({Naming.Literal(key)}, {Naming.Literal(name)}, "
                + $"{Naming.Literal(entity)}, WorkflowEvent.{EventName(@event)},");
            source.Indent();
            source.Line($"Field: {(field is null ? "null" : Naming.Literal(field))},");

            ConditionEmitter.TryEmit(workflow["when"], out var when);
            source.Line($"When: {when},");

            source.Line("Effects:");
            source.Line("[");
            source.Indent();
            foreach (var effect in effects) source.Line(effect + ",");
            source.Outdent();
            source.Line("]),");
            source.Outdent();
        }

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Workflows/AppWorkflows.cs", source.ToString());
    }

    /// <summary>The constant name rather than the string, so a typo in a trigger is a compile error
    /// in the generated application rather than a workflow that never fires.</summary>
    private static string EventName(string @event) => @event switch
    {
        "record.created" => "RecordCreated",
        "record.updated" => "RecordUpdated",
        "field.changed" => "FieldChanged",
        _ => "Schedule",
    };

    private static IEnumerable<string> Effects(AppModel app, string entity, JsonObject workflow)
    {
        foreach (var effect in AppModel.Arr(workflow["effects"]).OfType<JsonObject>())
        {
            var rendered = Effect(app, entity, effect);
            if (rendered is not null) yield return rendered;
        }
    }

    private static string? Effect(AppModel app, string entity, JsonObject effect) =>
        AppModel.Str(effect["type"]) switch
        {
            "notify" => $"new NotifyEffect({Naming.Literal(AppModel.Str(effect["to"]) ?? "")}, "
                + $"{Naming.Literal(AppModel.Str(effect["title"]) ?? "")}, "
                + $"{Naming.Literal(AppModel.Str(effect["message"]))}, "
                + $"{Naming.Literal(AppModel.Str(effect["link"]))})",

            "createRecord" when AppModel.Str(effect["entity"]) is { Length: > 0 } target =>
                $"new CreateRecordEffect({Naming.Literal(target)}, [{Sets(effect["set"])}])",

            "updateRecord" => Update(app, entity, effect),

            _ => null,
        };

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

    private static string Sets(JsonNode? set) =>
        set is not JsonObject fields
            ? ""
            : string.Join(", ", fields.Select(pair =>
                $"new EffectSet({Naming.Literal(pair.Key)}, {Naming.Literal(Scalar(pair.Value))})"));

    private static string Lower(bool value) => value ? "true" : "false";

    private static string? Scalar(JsonNode? value) => value?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}
