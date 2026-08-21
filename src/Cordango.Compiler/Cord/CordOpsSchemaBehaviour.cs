// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The behaviour authoring vocabulary, as provider-neutral JSON Schema.
///
/// <para><b>A SEPARATE schema, not an addition to the domain one</b> — rule 3. The model is given the
/// vocabulary for the task in front of it and never a union of every concern, because a union is how
/// the 208 KB <c>submit_candidate</c> schema came to exist in the first place. A test asserts no
/// behaviour operation is reachable from <see cref="CordOpsSchema.Domain"/>.</para>
///
/// <para><b>What is deliberately absent is the point of the slice.</b> There is no <c>command</c>
/// anywhere on this wire. A state change carries its own effects and the command is derived — 52 of 52
/// corpus commands belong to exactly one transition and share its entity, so the link is bookkeeping
/// rather than a decision. That removes, by construction: a transition pointing at another entity's
/// command, two transitions silently sharing one, and a command left orphaned when its transition
/// went.</para>
/// </summary>
internal static class CordOpsSchemaBehaviour
{
    public static JsonObject Behaviour() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("ops"),
        ["properties"] = new JsonObject
        {
            ["note"] = Str("One line on what this change is for. Shown to the person waiting."),
            ["ops"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                // The upsert rule is stated ONCE, here, rather than on each operation. Four repetitions
                // of "adds it, or replaces the one with this key" is 200 bytes of the same sentence, and
                // rule 3's answer to a schema that grew is to narrow before raising a ceiling.
                ["description"] =
                    "Applied in order. Either they all pass validation or none are kept. Each upsert "
                    + "adds one thing or replaces the one that already has its key, stated in full — "
                    + "anything omitted is cleared — and touches nothing else. Send several in one call.",
                ["items"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        UpsertLifecycle(), UpsertAction(), UpsertAutomation(), UpsertRole(),
                        RemoveBehaviour()),
                },
            },
        },
        ["$defs"] = new JsonObject
        {
            ["effect"] = Effect(),
            ["when"] = When(),
            ["confirm"] = Confirm(),
            ["ask"] = Ask(),
        },
    };

    private static JsonObject UpsertLifecycle() => Op("upsert_lifecycle",
        "The states one kind of record moves through, and what happens on each move, with its states and "
        + "transitions. Keyed by ENTITY — an entity has exactly one. "
        + "Use it when records have a life: open then won or lost, "
        + "draft then submitted then approved. A record with no meaningful states does not need one.",
        new JsonObject
        {
            ["lifecycle"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("entity", "stateField", "states"),
                ["properties"] = new JsonObject
                {
                    ["entity"] = Str("Entity key."),
                    ["key"] = Str("Identifier for this lifecycle. Defaults from the entity."),
                    ["stateField"] = Str(
                        "The field holding the state. It must be a select field on the entity, and its "
                        + "options are these states."),
                    ["initialState"] = Str("State key a new record starts in."),
                    ["states"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("key", "label"),
                            ["properties"] = new JsonObject
                            {
                                ["key"] = Str(null),
                                ["label"] = Str("What people call this state."),
                                ["color"] = Str("Hex colour for boards and chips."),
                                ["terminal"] = Bool("The record is finished here."),
                                ["phase"] = Enum(
                                    "Coarse grouping for reporting.",
                                    [.. CordVocabulary.StatePhases.Words]),
                            },
                        },
                    },
                    ["transitions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("key", "label", "from", "to"),
                            ["properties"] = new JsonObject
                            {
                                ["key"] = Str(null),
                                ["label"] = Str("The button people press. 'Mark as won', not 'win'."),
                                ["from"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["minItems"] = 1,
                                    ["items"] = Str(null),
                                    ["description"] = "State keys this move is allowed from.",
                                },
                                ["to"] = Str("State key this move lands in."),
                                ["requiredFields"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = Str(null),
                                    ["description"] =
                                        "Fields that must be filled before the move is allowed.",
                                },
                                ["icon"] = Str("Material symbol name."),
                                ["style"] = Enum("How prominent the button is.",
                                    [.. CordVocabulary.CommandStyles.Words]),
                                ["placements"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = Enum(null, [.. CordVocabulary.Placements.Words]),
                                    ["description"] = "Where the button appears.",
                                },
                                ["successMessage"] = Str("Shown after it runs."),
                                ["confirm"] = Ref("#/$defs/confirm"),
                                ["ask"] = Ref("#/$defs/ask"),
                                ["when"] = Ref("#/$defs/when"),
                                ["effects"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = Ref("#/$defs/effect"),
                                    ["description"] =
                                        "What happens on this move, BESIDES the state changing. The "
                                        + "state change itself is automatic — never write an effect "
                                        + "that sets the state field. Omit entirely when the move is "
                                        + "the whole story.",
                                },
                            },
                        },
                    },
                },
            },
        },
        "lifecycle");

    private static JsonObject UpsertAction() => Op("upsert_action",
        "One thing a person can do to a record that is NOT a state change — clone it, send it, "
        + "recalculate it. Anything that moves a record between states belongs in upsert_lifecycle "
        + "instead.",
        new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("key", "label", "entity"),
                ["properties"] = new JsonObject
                {
                    ["key"] = Str(null),
                    ["label"] = Str("The button people press."),
                    ["entity"] = Str("Entity key this acts on."),
                    ["description"] = Str("What it does, for whoever maintains this later."),
                    ["icon"] = Str("Material symbol name."),
                    ["style"] = Enum(null, [.. CordVocabulary.CommandStyles.Words]),
                    ["placements"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Enum(null, [.. CordVocabulary.Placements.Words]),
                    },
                    ["successMessage"] = Str("Shown after it runs."),
                    ["confirm"] = Ref("#/$defs/confirm"),
                    ["ask"] = Ref("#/$defs/ask"),
                    ["when"] = Ref("#/$defs/when"),
                    ["effects"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = Ref("#/$defs/effect"),
                    },
                },
            },
        },
        "action");

    private static JsonObject UpsertAutomation() => Op("upsert_automation",
        "One piece of work that happens with nobody watching, with its trigger and its effects: stamp a "
        + "timestamp when a reply arrives, notify an owner when something falls due.",
        new JsonObject
        {
            ["automation"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("key", "name", "on", "effects"),
                ["properties"] = new JsonObject
                {
                    ["key"] = Str(null),
                    ["name"] = Str("What this automation is for, in a few words."),
                    ["on"] = Enum(
                        "What sets it off. schedule.daily needs a cron; record.updated with a `field` "
                        + "means only when THAT field changes.",
                        [.. CordVocabulary.TriggerEvents.Words]),
                    ["entity"] = Str("The entity being watched."),
                    ["field"] = Str(
                        "For record.updated: only run when THIS field changed. Omit for any change."),
                    ["cron"] = Str("For schedule.daily: a cron expression."),
                    ["when"] = Ref("#/$defs/when"),
                    ["effects"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["items"] = Ref("#/$defs/effect"),
                    },
                },
            },
        },
        "automation");

    private static JsonObject UpsertRole() => Op("upsert_role",
        "One role, with its grants. A role with no grant for an entity cannot see that entity at all, so "
        + "list every entity this role needs.",
        new JsonObject
        {
            ["role"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("key", "name", "grants"),
                ["properties"] = new JsonObject
                {
                    ["key"] = Str(null),
                    ["name"] = Str("What this role is called."),
                    ["description"] = Str("What this role does and what it deliberately cannot."),
                    ["grants"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("entity", "read"),
                            ["properties"] = new JsonObject
                            {
                                ["entity"] = Str(null),
                                ["create"] = Bool(null),
                                ["read"] = Bool(null),
                                ["update"] = Bool(null),
                                ["delete"] = Bool(null),
                                ["commands"] = new JsonObject
                                {
                                    ["type"] = "array",
                                    ["items"] = Str(null),
                                    ["description"] =
                                        "Transition and action keys this role may run. A transition's "
                                        + "key is what you named it in upsert_lifecycle.",
                                },
                            },
                        },
                    },
                },
            },
        },
        "role");

    private static JsonObject RemoveBehaviour() => Op("remove_behaviour",
        "Drop one lifecycle, action, automation or role.",
        new JsonObject
        {
            ["kind"] = Enum(null, [.. CordBehaviourKinds.All]),
            ["key"] = Str("Its key — for a lifecycle, the ENTITY key."),
        },
        "kind", "key");

    // ---- shared shapes ---------------------------------------------------------------------------

    private static JsonObject Effect() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type"),
        ["description"] =
            "One thing that happens. Values may interpolate {{record.<field>}}, {{actor.id}}, "
            + "{{today}} and {{now}}.",
        ["properties"] = new JsonObject
        {
            ["type"] = Enum(null, [.. CordVocabulary.EffectTypes.Words]),
            ["target"] = Str(
                "updateRecord only: omit for this record, or name a reference field to update what it "
                + "points at."),
            ["set"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Field key to value.",
            },
            // Narrowed 2026-08-11 to pay for the transition guard, per rule 3. The cut half explained
            // the distinction ("stamping when the FIRST reply arrived versus restamping on every
            // reply"); the surviving half states it. Prose that RESTATES a property is what a ceiling
            // is for.
            ["setIfEmpty"] = Bool(
                "updateRecord only: fill only fields that are still empty — stamp the FIRST reply, "
                + "not every one."),
            ["entity"] = Str("createRecord only: what to create."),
            ["to"] = Str("notify only: who to tell, e.g. {{record.owner}}."),
            ["title"] = Str("notify only."),
            ["message"] = Str("notify only."),
            ["link"] = Str("notify only: 'auto' links to the record."),
        },
    };

    private static JsonObject When() => new()
    {
        ["type"] = "object",
        ["description"] =
            "A guard, checked on the server before this runs. One comparison, or `all` of several.",
        ["properties"] = new JsonObject
        {
            ["field"] = Str("Field key on the record."),
            ["operator"] = Enum(null, [.. CordVocabulary.ConditionOperators.Words]),
            ["value"] = new JsonObject
            {
                ["description"] =
                    "A literal, or {{actor.id}} for whoever is pressing the button. Guard every "
                    + "approval with {field: <who filed it>, operator: neq, value: '{{actor.id}}'} — "
                    + "a role cannot do this, because one person often holds two of them.",
            },
            ["all"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Ref("#/$defs/when"),
                ["description"] = "Every one of these must hold.",
            },
        },
    };

    private static JsonObject Confirm() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["description"] = "Ask before running. Use it for anything destructive or hard to undo.",
        ["properties"] = new JsonObject
        {
            ["title"] = Str(null),
            ["message"] = Str(null),
            // No description: the name says it, and a description that restates its own property
            // name costs the model attention to learn nothing. Narrowed 2026-08-13 to pay for
            // `contains`/`in`/`notIn` in the guard vocabulary, per rule 3 — the same trade `Ask`
            // made for the transition guard.
            ["confirmLabel"] = Str(null),
            ["tone"] = Enum("danger for anything destructive.", "normal", "danger"),
        },
    };

    /// <summary>The App Definition calls this <c>input</c>; Cord calls it <c>ask</c> because
    /// <c>input</c> is already a FIELD property meaning the widget to render. That etymology used to be
    /// in the schema description, where it cost the model attention to learn why a word it never sees is
    /// not the word it does see. Narrowed 2026-08-11 to pay for the transition guard, per rule 3.</summary>
    private static JsonObject Ask() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["description"] =
            "Collect these fields from the person before running — a reason, a date.",
        ["properties"] = new JsonObject
        {
            ["fields"] = new JsonObject { ["type"] = "array", ["items"] = Str(null) },
            ["required"] = new JsonObject { ["type"] = "array", ["items"] = Str(null) },
        },
    };

    // ---- helpers, mirroring CordOpsSchema so the two schemas read identically ---------------------

    private static JsonObject Op(string name, string description, JsonObject properties,
        params string[] required)
    {
        var props = new JsonObject { ["op"] = new JsonObject { ["const"] = name } };
        foreach (var (key, value) in properties.ToList())
        {
            properties.Remove(key);
            props[key] = value;
        }
        return new JsonObject
        {
            ["title"] = name,
            ["description"] = description,
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(new[] { "op" }.Concat(required).Select(r => (JsonNode)r!).ToArray()),
            ["properties"] = props,
        };
    }

    private static JsonObject Str(string? description)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Bool(string? description)
    {
        var o = new JsonObject { ["type"] = "boolean" };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Enum(string? description, params string[] values)
    {
        var o = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()),
        };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Ref(string pointer) => new() { ["$ref"] = pointer };
}
