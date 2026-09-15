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
///
/// <para><b>And nothing the runtime does goes unstated — but a rule may be stated as a rule.</b> The
/// CRUD events hold for every entity in every app, so they live in <c>eventDefaults</c> rather than
/// three times per entity in <c>events</c>. The reader still learns every event this app emits; it
/// reads one sentence and the entity list instead of a list that was three quarters boilerplate.
/// Absent values are omitted for the same reason — see <see cref="Prune"/>.</para>
/// </summary>
public static class AppContract
{
    /// <summary>
    /// The contract's own shape version. Independent of the App Definition schema version and of the
    /// manifest version, because it changes for different reasons than either.
    ///
    /// <para><b>1.1</b> made the document say only what is true of THIS app. A key whose value is
    /// absent — null, false, or an empty list — is omitted rather than spelled out, and the CRUD
    /// events every entity emits are stated once in <c>eventDefaults</c> instead of three times per
    /// entity. Both are subtractions of noise, not of meaning: a reader asking <c>field.required</c>
    /// or <c>field.options</c> gets the same answer it always did. A reader testing for the KEY's
    /// presence does not, which is why this is 1.1 and not more of 1.0.</para>
    /// </summary>
    public const string ContractVersion = "1.1";

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
            // Appended by ContractWriter.Seal over everything else, and absent until then. The hash
            // is ordinal-canonical, so where the key lands does not change what it says.
        };

        var actions = Actions(processes, commands);

        return Prune(new JsonObject
        {
            ["contractVersion"] = ContractVersion,
            ["kind"] = Kind,
            ["identity"] = identity,
            ["purpose"] = Purpose(definition),
            ["entities"] = new JsonArray([.. entities.OfType<JsonObject>().Select(Entity)]),
            ["dependencies"] = Dependencies(definition),
            ["eventDefaults"] = EventDefaults(),
            ["events"] = Events(processes, actions),
            ["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)a.Json)]),
            ["rules"] = Rules(actions),
        });
    }

    /// <summary>
    /// The contract with every absent value taken out — null, <c>false</c>, and empty lists.
    ///
    /// <para><b>A contract should say what is true of this app, not recite the whole vocabulary.</b>
    /// Every field carried <c>computed: false, targetApp: null, targetEntity: null, options: null</c>
    /// whether or not any of it applied; across a real app that was 490 null-valued keys in one
    /// document and roughly a third of its bytes. The cost is not disk — it is that everything
    /// downstream reads this, and an agent paying for context pays for the nulls too.</para>
    ///
    /// <para>Absence is the same answer the value was giving: a reader asking <c>field.required</c>
    /// or <c>field.options</c> gets null/undefined either way. Only a reader testing whether the KEY
    /// exists sees a difference, which is what the 1.1 in <see cref="ContractVersion"/> is for.</para>
    ///
    /// <para>Applied ONCE, here, rather than at each of the dozen places a key is built: a rule that
    /// has to be remembered at every call site is a rule that holds until the next section is
    /// added.</para>
    /// </summary>
    private static JsonObject Prune(JsonObject o)
    {
        foreach (var key in o.Select(p => p.Key).ToList())
        {
            var value = o[key];
            if (Empty(value)) { o.Remove(key); continue; }
            if (value is JsonObject child) Prune(child);
            else if (value is JsonArray array)
                foreach (var item in array.OfType<JsonObject>()) Prune(item);
        }
        return o;
    }

    /// <summary>Whether a value says nothing: absent, off, or an empty collection. A zero or an empty
    /// string is NOT nothing — somebody wrote them, and the contract reports what is there.</summary>
    private static bool Empty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => v.TryGetValue<bool>(out var b) && !b,
        _ => false,
    };

    /// <summary>
    /// The events every entity emits, stated once instead of three times per entity.
    ///
    /// <para><b>Said, not dropped.</b> The runtime emits <c>created</c>, <c>updated</c> and
    /// <c>deleted</c> for every entity in every app, so listing them per entity spends a quarter of
    /// the document restating one rule — 48 of budget-planner's 64 events carried no information a
    /// reader could not derive from the entity list. A contract that simply omitted them would break
    /// the promise that nothing the runtime does goes unpublished, so the rule itself is published
    /// and <see cref="Events"/> carries only what the rule cannot predict.</para>
    /// </summary>
    private static JsonObject EventDefaults() => new()
    {
        ["crud"] = true,
        ["kinds"] = new JsonArray(RecordCreated, RecordUpdated, RecordDeleted),
        ["note"] = "Every entity emits <entity>.created, <entity>.updated and <entity>.deleted. "
            + "The events list carries only what this rule does not already say.",
    };

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
        // What the entity is FOR, which a name does not carry. An agent told only that an app has an
        // `organization` still has to guess whether that is a customer, a supplier or a legal entity,
        // and a wrong guess is a second entity modelling the same thing.
        ["description"] = Str(e, "description"),
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
    /// <para>Two sources, two different answers to "what causes this". A state event happens because
    /// the record ENTERED a state — the actions listed against it can cause that, but they do not
    /// publish it. A command event is published by the action itself. An agent asking "what causes
    /// deal.won" needs that distinction: subscribing to it and calling the action are different
    /// plans.</para>
    ///
    /// <para>The third source, CRUD, is not listed here. It applies to every entity without
    /// exception, so it is stated once in <see cref="EventDefaults"/> and the reader derives the
    /// names from the entity list. What remains in this list is exactly what could not have been
    /// predicted — which is also what makes the list worth reading.</para>
    /// </summary>
    private static JsonArray Events(JsonArray processes, List<Action> actions)
    {
        var events = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, JsonObject body)
        {
            if (!seen.Add(name)) return;
            body["name"] = name;
            events.Add(body);
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
