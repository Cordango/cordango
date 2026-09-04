// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compile;

/// <summary>
/// The App Contract: what an application OFFERS, compiled beside the manifest.
///
/// <para><b>Two artifacts, opposite audiences.</b> The manifest serves the runtime and may change
/// shape whenever the compiler wants it to. The contract serves everything OUTSIDE the app — other
/// apps, agents, connectors, the marketplace — and its stability is a promise. Conflating them would
/// make every internal compiler tidy-up a breaking change to somebody's integration.</para>
///
/// <para><b>Derived, never authored, with exactly two exceptions</b> — <c>purpose</c> and
/// <c>uses</c>, which are in the definition because no compiler can work them out. A second
/// hand-written document would drift from the definition inside a week, so everything else here is a
/// projection: entities from what is declared, events from what the runtime demonstrably emits,
/// actions from the commands (authored and synthesized alike), rules from the guards that actually
/// run.</para>
///
/// <para><b>Host-independent, and that is load-bearing.</b> No app id, no handle, no tenant, no URL.
/// The same definition produces byte-identical bytes from <c>cordango build</c>, from the platform,
/// and from anywhere else, so <c>contractHash</c> means the same thing everywhere and two contracts
/// can be compared without knowing where either came from. Where an app can be REACHED is the host's
/// fact and belongs in the host's response envelope, never in this file.</para>
///
/// <para><b>Nothing is stated here that the runtime does not do.</b> Every event kind below is one
/// <c>AppDataService</c>/<c>CommandExecutor</c> actually writes, and every rule is one that actually
/// refuses a write. A contract listing an event nobody emits is worse than no contract at all: it is
/// a promise that fails silently, in someone else's app.</para>
/// </summary>
public static class AppContract
{
    /// <summary>The contract's own shape version. Independent of the App Definition schema version
    /// and of the manifest version, because it changes for different reasons than either.</summary>
    public const string ContractVersion = "1.0";

    public const string Kind = "app-contract";

    // Event types, mirroring AppEvent's constants in the runtime. Duplicated rather than shared
    // because the runtime is not an OSS dependency — the pair is pinned by a test on both sides.
    private const string RecordCreated = "record.created";
    private const string RecordUpdated = "record.updated";
    private const string RecordDeleted = "record.deleted";
    private const string StateEntered = "process.state_entered";
    private const string CommandEmitted = "command.emitted";

    /// <summary>The contract for a definition and the manifest it compiled to.</summary>
    /// <param name="definition">The BUILT definition — never a draft that has run ahead of the
    /// manifest, or the contract would describe code that is not running.</param>
    public static JsonObject Build(JsonObject definition, JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Compose(definition, manifest, DefinitionHash.Of(definition));
    }

    /// <summary>
    /// A contract for an app whose built definition was never recorded — everything the manifest can
    /// still answer, and no <c>definitionHash</c>, because there is no document to have hashed.
    ///
    /// <para>Honest rather than convenient: the manifest is the definition plus synthesis, so this
    /// says slightly more than the app declared and cannot prove which. Callers mark it provisional
    /// and replace it on the next build.</para>
    /// </summary>
    public static JsonObject FromManifest(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Compose(manifest, manifest, definitionHash: null);
    }

    private static JsonObject Compose(JsonObject definition, JsonObject manifest, string? definitionHash)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var entities = manifest["entities"] as JsonArray ?? [];
        var processes = manifest["processes"] as JsonArray ?? [];
        var commands = manifest["commands"] as JsonArray ?? [];

        var identity = new JsonObject
        {
            ["key"] = Str(definition, "key"),
            ["name"] = Str(definition, "name"),
            ["version"] = Str(definition, "version"),
            ["schemaVersion"] = Str(definition, "schemaVersion"),
            ["definitionHash"] = definitionHash,
            // Filled by ContractWriter.Seal over everything else. Present as null so the key order
            // of a sealed and an unsealed contract is the same.
            ["contractHash"] = null,
        };

        var actions = Actions(processes, commands);

        return new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["kind"] = Kind,
            ["identity"] = identity,
            ["purpose"] = Purpose(definition),
            ["entities"] = new JsonArray([.. entities.OfType<JsonObject>().Select(Entity)]),
            ["dependencies"] = Dependencies(definition),
            ["events"] = Events(entities, processes, commands, actions),
            ["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)a.Json)]),
            ["rules"] = Rules(actions),
        };
    }

    private static JsonNode? Purpose(JsonObject definition)
    {
        if (definition["purpose"] is not JsonObject p) return null;
        var o = new JsonObject { ["summary"] = Str(p, "summary") };
        if (p["duties"] is JsonArray duties && duties.Count > 0) o["duties"] = duties.DeepClone();
        return o;
    }

    private static JsonArray Dependencies(JsonObject definition) =>
        new([.. AppDependencies.Of(definition).Select(d => (JsonNode)new JsonObject
        {
            ["app"] = d.App,
            ["entities"] = new JsonArray([.. d.Entities.Select(e => (JsonNode)e)]),
            ["source"] = d.Source,
            ["fields"] = new JsonArray([.. d.Fields.Select(f => (JsonNode)f)]),
            ["why"] = d.Why,
        })]);

    /// <summary>One entity as something outside the app can talk about: what it is called, and what a
    /// caller may put in each field. Types, not just names — a matcher pairing two fields has to know
    /// whether they can hold the same thing. System fields are the runtime's and are left out.</summary>
    private static JsonObject Entity(JsonObject e) => new()
    {
        ["key"] = Str(e, "key"),
        ["label"] = Str(e, "label"),
        ["labelPlural"] = Str(e, "labelPlural"),
        ["displayField"] = Str(e, "displayField"),
        ["kind"] = Str(e, "kind"),
        ["ownedBy"] = e["ownedBy"] is JsonObject owned ? Str(owned, "parent") : null,
        ["fields"] = new JsonArray([.. (e["fields"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(f => f["system"]?.GetValue<bool>() != true)
            .Select(f => (JsonNode)new JsonObject
            {
                ["key"] = Str(f, "key"),
                ["label"] = Str(f, "label"),
                ["type"] = Str(f, "type"),
                ["required"] = f["required"]?.GetValue<bool>() == true,
                // A computed field is the server's to write. Saying so stops a caller building a
                // request around a value it is not allowed to send.
                ["computed"] = f["computed"] is not null || f["expr"] is not null,
                ["targetApp"] = Str(f, "targetApp"),
                ["targetEntity"] = Str(f, "targetEntity"),
                ["options"] = f["options"] is JsonArray opts
                    ? new JsonArray([.. opts.OfType<JsonObject>().Select(o => (JsonNode)new JsonObject
                    {
                        ["value"] = Str(o, "value"),
                        ["label"] = Str(o, "label"),
                        ["color"] = Str(o, "color"),
                    })])
                    : null,
            })]),
    };

    /// <summary>An action and the facts about it every other section needs — kept together so the
    /// events it causes and the rules it must satisfy cannot disagree with the action itself.</summary>
    private sealed record Action(
        string Id, string Key, string Entity, JsonObject Json,
        string? StateEntered, IReadOnlyList<string> Emits);

    private static List<Action> Actions(JsonArray processes, JsonArray commands)
    {
        // transition key -> (process key, the transition), per entity.
        var transitions = new Dictionary<string, (string? Process, JsonObject Transition)>(StringComparer.Ordinal);
        foreach (var p in processes.OfType<JsonObject>())
        {
            var entity = Str(p, "entity");
            if (entity is null) continue;
            foreach (var t in (p["transitions"] as JsonArray ?? []).OfType<JsonObject>())
                if (Str(t, "key") is { } tk) transitions[$"{entity}|{tk}"] = (Str(p, "key"), t);
        }

        var actions = new List<Action>();
        foreach (var c in commands.OfType<JsonObject>())
        {
            var entity = Str(c, "entity");
            var key = Str(c, "key");
            if (entity is null || key is null) continue;

            transitions.TryGetValue($"{entity}|{Str(c, "transition")}", out var bound);
            var transition = bound.Transition;
            var to = transition is null ? null : Str(transition, "to");

            var required = Required(c, transition);
            var emits = (c["emits"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().ToList();
            var id = $"{entity}.{key}";

            var json = new JsonObject
            {
                ["id"] = id,
                ["key"] = key,
                ["entity"] = entity,
                ["label"] = Str(c, "label"),
                ["description"] = Str(c, "description"),
                // A synthesized action is one the compiler wrote for a transition nobody bound a
                // command to. It is as real as any other; the flag says where it came from.
                ["synthesized"] = c["synthesized"]?.GetValue<bool>() == true,
                ["transition"] = transition is null ? null : new JsonObject
                {
                    ["key"] = Str(c, "transition"),
                    ["process"] = bound.Process,
                    ["from"] = (transition["from"] as JsonArray)?.DeepClone(),
                    ["to"] = to,
                },
                ["input"] = c["input"] is JsonObject input ? new JsonObject
                {
                    ["fields"] = (input["fields"] as JsonArray)?.DeepClone(),
                    ["required"] = (input["required"] as JsonArray)?.DeepClone(),
                } : null,
                ["requires"] = new JsonArray([.. RuleIds(entity, key, transition, Str(c, "transition"), c, required)
                    .Select(r => (JsonNode)r)]),
                ["emits"] = new JsonArray([.. emits.Select(n => (JsonNode)n)]),
                ["causes"] = new JsonArray([.. Causes(entity, to, c).Select(n => (JsonNode)n)]),
            };

            actions.Add(new Action(id, key, entity, json, to, emits));
        }

        return [.. actions.OrderBy(a => a.Id, StringComparer.Ordinal)];
    }

    /// <summary>The fields a command refuses to run without: the transition's and its own, exactly as
    /// <c>CommandExecutor</c> unions them.</summary>
    private static List<string> Required(JsonObject command, JsonObject? transition)
    {
        var required = new List<string>();
        foreach (var r in transition?["requiredFields"] as JsonArray ?? [])
            if (r?.GetValue<string>() is { } k && !required.Contains(k)) required.Add(k);
        foreach (var r in command["input"]?["required"] as JsonArray ?? [])
            if (r?.GetValue<string>() is { } k && !required.Contains(k)) required.Add(k);
        return required;
    }

    /// <summary>What running this action can make the runtime announce, beyond what the action
    /// announces itself. A bound action moves the state, and a state move is a write.</summary>
    private static List<string> Causes(string entity, string? to, JsonObject command)
    {
        var causes = new List<string>();
        if (to is not null) causes.Add($"{entity}.{to}");
        if (to is not null || command["input"]?["fields"] is JsonArray { Count: > 0 })
            causes.Add($"{entity}.updated");
        return causes;
    }

    /// <summary>Every rule id that governs one action, in the order they are checked.</summary>
    private static List<string> RuleIds(string entity, string commandKey, JsonObject? transition,
        string? transitionKey, JsonObject command, IReadOnlyList<string> required)
    {
        var ids = new List<string>();
        if (command["when"] is JsonObject) ids.Add($"{entity}.{commandKey}.when");
        if (transitionKey is not null && transition is not null)
        {
            ids.Add($"{entity}.{transitionKey}.from");
            // Distinct from the command's guard on purpose: they can both be present, they refuse
            // with different codes, and one id for two rules would make a failure unattributable.
            if (transition["when"] is JsonObject) ids.Add($"{entity}.{transitionKey}.when");
        }
        ids.AddRange(required.Select(f => $"{entity}.{commandKey}.requires_{f}"));
        return ids;
    }

    /// <summary>
    /// The rules themselves — the condition, not only its name.
    ///
    /// <para>An index of rule ids would tell an agent that <c>deal.mark_won.requires_value</c> exists
    /// and nothing about what it wants. Every rule below carries the fact it asserts, so a caller can
    /// decide whether it will pass before it tries.</para>
    /// </summary>
    private static JsonArray Rules(List<Action> actions)
    {
        var rules = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(JsonObject rule)
        {
            if (rule["id"]?.GetValue<string>() is { } id && seen.Add(id)) rules.Add(rule);
        }

        foreach (var action in actions)
        {
            var c = action.Json;
            var entity = action.Entity;
            var on = new JsonArray($"command:{action.Key}");

            foreach (var id in (c["requires"] as JsonArray ?? []).Select(x => x?.GetValue<string>()).OfType<string>())
            {
                if (id.EndsWith(".from", StringComparison.Ordinal) && c["transition"] is JsonObject t)
                {
                    Add(new JsonObject
                    {
                        ["id"] = id,
                        ["entity"] = entity,
                        ["on"] = on.DeepClone(),
                        ["kind"] = "state",
                        ["effect"] = "stop",
                        ["process"] = t["process"]?.DeepClone(),
                        ["from"] = t["from"]?.DeepClone(),
                        ["to"] = t["to"]?.DeepClone(),
                        ["source"] = "synthesized",
                    });
                }
                else if (id.EndsWith(".when", StringComparison.Ordinal))
                {
                    // Which `when` this id names is decided by the id itself: the transition's id
                    // carries the transition key, the command's carries the command key.
                    var isTransition = c["transition"] is JsonObject bound
                        && id == $"{entity}.{bound["key"]?.GetValue<string>()}.when";
                    Add(new JsonObject
                    {
                        ["id"] = id,
                        ["entity"] = entity,
                        ["on"] = on.DeepClone(),
                        ["kind"] = "guard",
                        ["effect"] = "stop",
                        ["condition"] = isTransition ? null : c["when"]?.DeepClone(),
                        ["source"] = "synthesized",
                    });
                }
                else if (id.Contains(".requires_", StringComparison.Ordinal))
                {
                    var field = id[(id.IndexOf(".requires_", StringComparison.Ordinal) + ".requires_".Length)..];
                    Add(new JsonObject
                    {
                        ["id"] = id,
                        ["entity"] = entity,
                        ["on"] = on.DeepClone(),
                        ["kind"] = "required",
                        ["effect"] = "stop",
                        ["assertion"] = new JsonObject { ["fields"] = new JsonArray(field) },
                        ["source"] = "synthesized",
                    });
                }
            }
        }

        return new JsonArray([.. rules.OrderBy(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(r => (JsonNode)r)]);
    }

    /// <summary>
    /// Every event this app can emit, and how each one comes to exist.
    ///
    /// <para>Three sources, three different answers to "what causes this". A CRUD event happens on
    /// any write. A state event happens because the record ENTERED a state — the actions listed
    /// against it can cause that, but they do not publish it. A command event is published by the
    /// action itself. An agent asking "what causes deal.won" needs that distinction: subscribing to
    /// it and calling the action are different plans.</para>
    /// </summary>
    private static JsonArray Events(JsonArray entities, JsonArray processes, JsonArray commands,
        List<Action> actions)
    {
        var events = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, JsonObject body)
        {
            if (!seen.Add(name)) return;
            body["name"] = name;
            events.Add(body);
        }

        foreach (var e in entities.OfType<JsonObject>())
        {
            if (Str(e, "key") is not { } key) continue;
            var label = Str(e, "label") ?? key;
            Add($"{key}.created", new JsonObject
            {
                ["type"] = RecordCreated, ["entity"] = key, ["description"] = $"A {label} was created",
            });
            Add($"{key}.updated", new JsonObject
            {
                ["type"] = RecordUpdated, ["entity"] = key, ["description"] = $"A {label} changed",
            });
            Add($"{key}.deleted", new JsonObject
            {
                ["type"] = RecordDeleted, ["entity"] = key, ["description"] = $"A {label} was deleted",
            });
        }

        foreach (var p in processes.OfType<JsonObject>())
        {
            if (Str(p, "entity") is not { } entity) continue;
            foreach (var s in (p["states"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Str(s, "key") is not { } state) continue;
                Add($"{entity}.{state}", new JsonObject
                {
                    ["type"] = StateEntered,
                    ["entity"] = entity,
                    ["process"] = Str(p, "key"),
                    ["state"] = state,
                    ["description"] = $"A {entity} entered '{Str(s, "label") ?? state}'",
                    // Caused by, not emitted by: the runtime publishes this because the state
                    // changed, whoever changed it.
                    ["causedByActions"] = new JsonArray([.. actions
                        .Where(a => a.Entity == entity && a.StateEntered == state)
                        .Select(a => (JsonNode)a.Id)]),
                });
            }
        }

        foreach (var name in actions.SelectMany(a => a.Emits).Distinct(StringComparer.Ordinal)
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var by = actions.Where(a => a.Emits.Contains(name)).ToList();
            Add(name, new JsonObject
            {
                ["type"] = CommandEmitted,
                ["entity"] = by[0].Entity,
                ["description"] = $"Announced by {string.Join(", ", by.Select(a => $"'{a.Id}'"))}",
                ["emittedBy"] = new JsonArray([.. by.Select(a => (JsonNode)a.Id)]),
            });
        }

        return new JsonArray([.. events.OrderBy(e => e["name"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(e => (JsonNode)e)]);
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
