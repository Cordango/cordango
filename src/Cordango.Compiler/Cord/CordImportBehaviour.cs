// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The behaviour half of <see cref="CordImport"/>: <c>processes</c>, <c>commands</c>, <c>workflows</c>
/// and <c>roles</c>.
///
/// <para><b>The interesting part is that commands are not imported as commands.</b> A command named by
/// a transition is folded INTO that transition, because the corpus says the two are one thing filed as
/// two: 52 of 52 transition-linked commands are used by exactly one transition and carry their
/// process's entity. Only the 8 that no transition names survive as <see cref="CordAction"/>.</para>
///
/// <para>Everything here obeys the same totality rule as the domain importer — claim a property only
/// when it has the expected shape, and when a construct is not what we think it is, leave the WHOLE
/// construct for the overlay rather than modelling it half-right. The all-or-nothing decision is made
/// at the section level for the same reason arrays are: a list with holes cannot round-trip
/// positionally.</para>
/// </summary>
internal static class CordImportBehaviour
{
    /// <summary>Everything behavioural, claimed off <paramref name="rest"/> in one pass.
    ///
    /// <para>Processes and commands are read TOGETHER and not separately, which is the whole point:
    /// deciding whether a command is an action requires knowing every transition first. Either both
    /// sections import or neither does — a half-import would leave a transition pointing at a command
    /// that had already become an action.</para></summary>
    public static (List<CordProcess>? Processes, List<CordAction>? Actions,
                   List<CordSchedule>? Schedules, List<CordRole>? Roles) Take(JsonObject rest)
    {
        var (processes, actions) = ProcessesAndCommands(rest);
        return (processes, actions, Schedules(rest), Roles(rest));
    }

    private static (List<CordProcess>?, List<CordAction>?) ProcessesAndCommands(JsonObject rest)
    {
        var processNode = rest["processes"];
        var commandNode = rest["commands"];

        // Absent is fine; present-but-not-an-array-of-objects is not ours to touch.
        if (processNode is not null && processNode is not JsonArray) return (null, null);
        if (commandNode is not null && commandNode is not JsonArray) return (null, null);
        if (processNode is null && commandNode is null) return (null, null);

        var processArr = processNode as JsonArray ?? [];
        var commandArr = commandNode as JsonArray ?? [];
        if (!processArr.All(x => x is JsonObject) || !commandArr.All(x => x is JsonObject))
            return (null, null);

        // Keyed by command key, and DUPLICATES DISQUALIFY the whole section. Two commands sharing a key
        // is not a document this layer should be quietly rewriting: folding one into a transition would
        // silently pick a winner, and the round-trip would then "prove" a document nobody wrote.
        var byKey = new Dictionary<string, (JsonObject Obj, int Index)>(StringComparer.Ordinal);
        for (var i = 0; i < commandArr.Count; i++)
        {
            var obj = (JsonObject)commandArr[i]!;
            if (obj["key"] is not JsonValue v || !v.TryGetValue<string>(out var k)) return (null, null);
            if (!byKey.TryAdd(k, (obj, i))) return (null, null);
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var processes = new List<CordProcess>();

        foreach (var node in processArr)
        {
            var src = (JsonObject)((JsonObject)node!).DeepClone();
            var entity = CordJson.TakeString(src, "entity");

            List<CordTransition>? transitions = null;
            if (CordJson.TakeArray(src, "transitions") is { } tArr)
            {
                if (tArr.All(x => x is JsonObject))
                {
                    transitions = [];
                    foreach (var t in tArr)
                        transitions.Add(Transition((JsonObject)t!, entity, byKey, claimed));
                }
                else
                {
                    src["transitions"] = tArr;
                }
            }

            processes.Add(new CordProcess(
                CordJson.TakeString(src, "key"),
                entity,
                CordJson.TakeString(src, "stateField"),
                CordJson.TakeString(src, "initialState"),
                States(src),
                transitions,
                CordJson.Remainder(src)));
        }

        // Whatever no transition claimed is a standalone action, and it keeps its INDEX. Rebuilding
        // "transitions in order, then the rest" reproduced only 10 of 13 corpus apps; carrying the index
        // reproduced 13 of 13. Array order is meaningful to DefinitionHash, so this is load-bearing.
        var actions = new List<CordAction>();
        foreach (var (key, (obj, index)) in byKey.OrderBy(p => p.Value.Index))
        {
            if (claimed.Contains(key)) continue;
            actions.Add(Action((JsonObject)obj.DeepClone(), index));
        }

        rest.Remove("processes");
        rest.Remove("commands");

        // A document with commands but NO processes must not gain an empty `processes: []` — that is
        // Cord inventing a section, and the round trip catches it as an ADDED node. Same the other way.
        // (finance.appdef.json is the corpus's standalone-commands-only app and is why this exists.)
        return (processNode is null ? null : processes,
                commandNode is null ? null : actions);
    }

    private static CordTransition Transition(JsonObject src, string? processEntity,
        Dictionary<string, (JsonObject Obj, int Index)> byKey, HashSet<string> claimed)
    {
        var rest = (JsonObject)src.DeepClone();
        var key = CordJson.TakeString(rest, "key");
        var label = CordJson.TakeString(rest, "label");
        var commandKey = CordJson.TakeString(rest, "command");

        var from = Strings(rest, "from");
        var to = CordJson.TakeString(rest, "to");
        var requiredFields = Strings(rest, "requiredFields");

        // No command, or one this transition may not fold in: keep the transition, put the name back.
        // A transition naming a command that another transition already took is exactly the shared
        // command the 52/52 measurement says does not happen — but if it ever does, the honest answer
        // is to leave both alone rather than to fold one and orphan the other.
        if (commandKey is null || !byKey.TryGetValue(commandKey, out var found) || !claimed.Add(commandKey))
        {
            if (commandKey is not null) rest["command"] = commandKey;
            return new CordTransition(key, label, from, to,
                RequiredFields: requiredFields, Raw: CordJson.Remainder(rest));
        }

        var cmd = (JsonObject)found.Obj.DeepClone();
        var cmdKey = CordJson.TakeString(cmd, "key");
        var cmdLabel = CordJson.TakeString(cmd, "label");
        var cmdEntity = CordJson.TakeString(cmd, "entity");

        // The entity is derived from the process (52/52 in the corpus). If a document disagrees, this is
        // not the 1:1 construct being modelled — unfold it and leave both sides untouched.
        if (cmdEntity != processEntity)
        {
            claimed.Remove(commandKey);
            rest["command"] = commandKey;
            return new CordTransition(key, label, from, to,
                RequiredFields: requiredFields, Raw: CordJson.Remainder(rest));
        }

        var icon = CordJson.TakeString(cmd, "icon");
        var style = CordJson.TakeString(cmd, "style");
        var placements = Strings(cmd, "placements");
        var successMessage = CordJson.TakeString(cmd, "successMessage");
        var confirm = Confirm(cmd);
        var ask = Ask(cmd);
        var when = When(cmd, "when");
        var effects = Effects(cmd, "effects");

        // Anything still on the command has no home on a transition — `description` and `emits` occur on
        // a handful. Rather than drop them, UNFOLD: put the name back on the transition and let
        // the command survive as a standalone action at its own index. The round-trip stays exact and
        // coverage reports the loss honestly, which is the trade this layer makes everywhere.
        if (CordJson.Remainder(cmd) is not null)
        {
            claimed.Remove(commandKey);
            rest["command"] = commandKey;
            return new CordTransition(key, label, from, to,
                RequiredFields: requiredFields, Raw: CordJson.Remainder(rest));
        }

        return new CordTransition(
            key, label, from, to,
            // Only when the default — the transition's own key — would not reproduce it. 36 of 52.
            CommandKey: cmdKey == key ? null : cmdKey,
            // Same rule: the label matched the transition's in 50 of 52.
            CommandLabel: cmdLabel == label ? null : cmdLabel,
            icon, style, placements, successMessage,
            confirm, ask,
            requiredFields,
            when,
            effects,
            CordJson.Remainder(rest));
    }

    private static CordAction Action(JsonObject src, int index)
    {
        var rest = src;
        return new CordAction(
            CordJson.TakeString(rest, "key"),
            CordJson.TakeString(rest, "label"),
            CordJson.TakeString(rest, "entity"),
            index,
            CordJson.TakeString(rest, "description"),
            CordJson.TakeString(rest, "icon"),
            CordJson.TakeString(rest, "style"),
            Strings(rest, "placements"),
            CordJson.TakeString(rest, "successMessage"),
            Confirm(rest),
            Ask(rest),
            When(rest, "when"),
            Effects(rest, "effects"),
            CordJson.Remainder(rest));
    }

    private static List<CordState>? States(JsonObject rest)
    {
        if (rest["states"] is not JsonArray arr) return null;
        var states = new List<CordState>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) return null;
            var copy = (JsonObject)o.DeepClone();
            var key = CordJson.TakeString(copy, "key");
            var label = CordJson.TakeString(copy, "label");
            if (key is null || label is null) return null;
            states.Add(new CordState(key, label,
                CordJson.TakeString(copy, "color"),
                CordJson.TakeBool(copy, "terminal"),
                CordJson.TakeString(copy, "phase"),
                CordJson.Remainder(copy)));
        }
        rest.Remove("states");
        return states;
    }

    private static List<CordSchedule>? Schedules(JsonObject rest)
    {
        if (rest["workflows"] is not JsonArray arr) return null;
        if (!arr.All(x => x is JsonObject)) return null;

        var schedules = new List<CordSchedule>();
        foreach (var item in arr)
        {
            var copy = (JsonObject)((JsonObject)item!).DeepClone();
            schedules.Add(new CordSchedule(
                CordJson.TakeString(copy, "key"),
                CordJson.TakeString(copy, "name"),
                Trigger(copy),
                When(copy, "when"),
                Effects(copy, "effects"),
                CordJson.Remainder(copy)));
        }
        rest.Remove("workflows");
        return schedules;
    }

    private static CordTrigger? Trigger(JsonObject rest)
    {
        if (rest["trigger"] is not JsonObject o) return null;
        var copy = (JsonObject)o.DeepClone();
        var trigger = new CordTrigger(
            CordJson.TakeString(copy, "event"),
            CordJson.TakeString(copy, "entity"),
            CordJson.TakeString(copy, "field"),
            CordJson.TakeString(copy, "cron"),
            CordJson.Remainder(copy));
        rest.Remove("trigger");
        return trigger;
    }

    private static List<CordRole>? Roles(JsonObject rest)
    {
        if (rest["roles"] is not JsonArray arr) return null;
        if (!arr.All(x => x is JsonObject)) return null;

        var roles = new List<CordRole>();
        foreach (var item in arr)
        {
            var copy = (JsonObject)((JsonObject)item!).DeepClone();
            roles.Add(new CordRole(
                CordJson.TakeString(copy, "key"),
                CordJson.TakeString(copy, "name"),
                CordJson.TakeString(copy, "description"),
                Grants(copy),
                CordJson.Remainder(copy)));
        }
        rest.Remove("roles");
        return roles;
    }

    private static List<CordGrant>? Grants(JsonObject rest)
    {
        if (rest["grants"] is not JsonArray arr) return null;
        if (!arr.All(x => x is JsonObject)) return null;

        var grants = new List<CordGrant>();
        foreach (var item in arr)
        {
            var copy = (JsonObject)((JsonObject)item!).DeepClone();
            grants.Add(new CordGrant(
                CordJson.TakeString(copy, "entity"),
                CordJson.TakeBool(copy, "create"),
                CordJson.TakeBool(copy, "read"),
                CordJson.TakeBool(copy, "update"),
                CordJson.TakeBool(copy, "delete"),
                Strings(copy, "commands"),
                CordJson.Remainder(copy)));
        }
        rest.Remove("grants");
        return grants;
    }

    private static CordConfirm? Confirm(JsonObject rest)
    {
        if (rest["confirm"] is not JsonObject o) return null;
        var copy = (JsonObject)o.DeepClone();
        var confirm = new CordConfirm(
            CordJson.TakeString(copy, "title"),
            CordJson.TakeString(copy, "message"),
            CordJson.TakeString(copy, "confirmLabel"),
            CordJson.TakeString(copy, "tone"),
            CordJson.Remainder(copy));
        rest.Remove("confirm");
        return confirm;
    }

    private static CordAsk? Ask(JsonObject rest)
    {
        if (rest["input"] is not JsonObject o) return null;
        var copy = (JsonObject)o.DeepClone();
        var ask = new CordAsk(
            Strings(copy, "fields"),
            Strings(copy, "required"),
            CordJson.Remainder(copy));
        rest.Remove("input");
        return ask;
    }

    private static CordWhen? When(JsonObject rest, string key)
    {
        if (rest[key] is not JsonObject o) return null;
        var when = WhenOf((JsonObject)o.DeepClone());
        if (when is null) return null;
        rest.Remove(key);
        return when;
    }

    private static CordWhen? WhenOf(JsonObject copy)
    {
        List<CordWhen>? all = null;
        if (copy["all"] is JsonArray arr)
        {
            if (!arr.All(x => x is JsonObject)) return null;
            all = [];
            foreach (var item in arr)
            {
                var sub = WhenOf((JsonObject)((JsonObject)item!).DeepClone());
                if (sub is null) return null;
                all.Add(sub);
            }
            copy.Remove("all");
        }

        return new CordWhen(
            CordJson.TakeString(copy, "field"),
            CordJson.TakeString(copy, "operator"),
            CordJson.TakeNode(copy, "value"),
            all,
            CordJson.Remainder(copy));
    }

    private static List<CordEffect>? Effects(JsonObject rest, string key)
    {
        if (rest[key] is not JsonArray arr) return null;
        if (!arr.All(x => x is JsonObject)) return null;

        var effects = new List<CordEffect>();
        foreach (var item in arr)
        {
            var copy = (JsonObject)((JsonObject)item!).DeepClone();
            effects.Add(new CordEffect(
                CordJson.TakeString(copy, "type"),
                CordJson.TakeNode(copy, "target"),
                CordJson.TakeObject(copy, "set"),
                CordJson.TakeBool(copy, "setIfEmpty"),
                CordJson.TakeString(copy, "entity"),
                CordJson.TakeString(copy, "source"),
                CordJson.TakeString(copy, "key"),
                CordJson.TakeString(copy, "to"),
                CordJson.TakeString(copy, "title"),
                CordJson.TakeString(copy, "message"),
                CordJson.TakeString(copy, "link"),
                CordJson.Remainder(copy)));
        }
        rest.Remove(key);
        return effects;
    }

    private static List<string>? Strings(JsonObject rest, string key)
    {
        if (rest[key] is not JsonArray arr) return null;
        var list = new List<string>();
        foreach (var item in arr)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            list.Add(s);
        }
        rest.Remove(key);
        return list;
    }
}
