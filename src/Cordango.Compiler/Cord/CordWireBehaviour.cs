// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The behaviour wire shape → the model, following the same rules as <see cref="CordWire"/>: the
/// spelling differs from the App Definition wherever the difference removes a decision.
///
/// <list type="bullet">
/// <item>A transition carries its own <c>effects</c>. There is no <c>command</c> on the wire at all —
/// the command is derived, exactly as a rollup's <c>via</c> is, and 52 of 52 corpus commands say that
/// derivation is sound.</item>
/// <item><c>ask</c> rather than <c>input</c>, because <c>input</c> already means "which widget" on a
/// field and one word meaning two things is a trap.</item>
/// <item><c>on</c> rather than <c>trigger.event</c>, flattened: an automation answers "when does this
/// run" with one string plus the entity it watches.</item>
/// </list>
/// </summary>
internal static class CordWireBehaviour
{
    public static CordProcess Process(JsonObject o) => new(
        Key: Str(o, "key"),
        Entity: Str(o, "entity"),
        StateField: Str(o, "stateField"),
        InitialState: Str(o, "initialState"),
        States: o["states"] is JsonArray s ? s.OfType<JsonObject>().Select(State).ToList() : null,
        Transitions: o["transitions"] is JsonArray t
            ? t.OfType<JsonObject>().Select(Transition).ToList()
            : null);

    private static CordState State(JsonObject o) => new(
        Str(o, "key") ?? "", Str(o, "label") ?? "",
        Str(o, "color"), Bool(o, "terminal"), Str(o, "phase"));

    private static CordTransition Transition(JsonObject o) => new(
        Key: Str(o, "key"),
        Label: Str(o, "label"),
        From: Strings(o, "from"),
        To: Str(o, "to"),
        // Command identity is derived for model-authored work. Imported overrides are exposed only
        // by the human advanced editor, never as another join the model has to maintain.
        Icon: Str(o, "icon"),
        Style: Str(o, "style"),
        Placements: Strings(o, "placements"),
        SuccessMessage: Str(o, "successMessage"),
        Confirm: o["confirm"] is JsonObject c ? Confirm(c) : null,
        Ask: o["ask"] is JsonObject a ? Ask(a) : null,
        RequiredFields: Strings(o, "requiredFields"),
        When: o["when"] is JsonObject w ? When(w) : null,
        Effects: o["effects"] is JsonArray e ? e.OfType<JsonObject>().Select(Effect).ToList() : null);

    public static CordAction Action(JsonObject o) => new(
        Key: Str(o, "key"),
        Label: Str(o, "label"),
        Entity: Str(o, "entity"),
        // `At` is import bookkeeping and never authored — a Cord-written action is appended.
        At: null,
        Description: Str(o, "description"),
        Icon: Str(o, "icon"),
        Style: Str(o, "style"),
        Placements: Strings(o, "placements"),
        SuccessMessage: Str(o, "successMessage"),
        Confirm: o["confirm"] is JsonObject c ? Confirm(c) : null,
        Ask: o["ask"] is JsonObject a ? Ask(a) : null,
        When: o["when"] is JsonObject w ? When(w) : null,
        Effects: o["effects"] is JsonArray e ? e.OfType<JsonObject>().Select(Effect).ToList() : null);

    public static CordSchedule Schedule(JsonObject o) => new(
        Key: Str(o, "key"),
        Name: Str(o, "name"),
        // Flattened on the wire: `on` + `entity` + optional `field`/`cron`, rather than a nested
        // trigger object whose only job is to hold them.
        Trigger: new CordTrigger(Str(o, "on"), Str(o, "entity"), Str(o, "field"), Str(o, "cron")),
        When: o["when"] is JsonObject w ? When(w) : null,
        Effects: o["effects"] is JsonArray e ? e.OfType<JsonObject>().Select(Effect).ToList() : null);

    public static CordRole Role(JsonObject o) => new(
        Key: Str(o, "key"),
        Name: Str(o, "name"),
        Description: Str(o, "description"),
        Grants: o["grants"] is JsonArray g ? g.OfType<JsonObject>().Select(Grant).ToList() : null);

    private static CordGrant Grant(JsonObject o) => new(
        Str(o, "entity"), Bool(o, "create"), Bool(o, "read"), Bool(o, "update"), Bool(o, "delete"),
        Strings(o, "commands"));

    private static CordConfirm Confirm(JsonObject o) => new(
        Str(o, "title"), Str(o, "message"), Str(o, "confirmLabel"), Str(o, "tone"));

    private static CordAsk Ask(JsonObject o) => new(Strings(o, "fields"), Strings(o, "required"));

    private static CordWhen When(JsonObject o) => new(
        Str(o, "field"), Str(o, "operator"), o["value"]?.DeepClone(),
        o["all"] is JsonArray all ? all.OfType<JsonObject>().Select(When).ToList() : null);

    private static CordEffect Effect(JsonObject o) => new(
        Type: Str(o, "type"),
        Target: o["target"]?.DeepClone(),
        Set: o["set"] as JsonObject is { } set ? (JsonObject)set.DeepClone() : null,
        SetIfEmpty: Bool(o, "setIfEmpty"),
        Entity: Str(o, "entity"),
        Source: Str(o, "source"),
        Key: Str(o, "key"),
        To: Str(o, "to"),
        Title: Str(o, "title"),
        Message: Str(o, "message"),
        Link: Str(o, "link"));

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static List<string>? Strings(JsonObject o, string key) =>
        o[key] is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s is not null).Select(s => s!).ToList()
            : null;
}
