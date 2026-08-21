// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The behaviour half of <see cref="CordLower"/>: <see cref="CordProcess"/>, <see cref="CordAction"/>,
/// <see cref="CordSchedule"/> and <see cref="CordRole"/> → <c>processes</c>, <c>commands</c>,
/// <c>workflows</c>, <c>roles</c>.
///
/// <para><b>This is where one semantic statement becomes two filed objects.</b> A transition that says
/// "when a deal is won, stamp the date and tell the owner" lowers to a <c>transitions[]</c> entry
/// naming a command AND the <c>commands[]</c> entry carrying the effects. The author writes neither the
/// command key nor the link, which is what makes a dangling or shared command unreachable from
/// Cord.</para>
///
/// <para>Like the rest of the lowerer this is a pure function of <see cref="CordApp"/> — rule 2. It
/// never consults the document an app was imported from, which is what keeps a future where
/// <c>*.cord</c> is the source rather than the derivative.</para>
/// </summary>
internal static class CordLowerBehaviour
{
    public static void Emit(CordApp app, JsonObject doc, List<string> semantic, List<string> raw)
    {
        void Put(string pointer, string key, JsonNode? value, JsonObject into)
        {
            if (value is null) return;
            into[key] = value;
            semantic.Add(CordJson.Pointer(pointer, key));
        }

        void Overlay(string pointer, JsonObject? overlay, JsonObject into)
        {
            if (overlay is null) return;
            foreach (var (key, _) in overlay) raw.Add(CordJson.Pointer(pointer, key));
            CordJson.Merge(into, overlay);
        }

        // ---- processes, and the commands they imply ------------------------------------------------

        // Built before either array is written, because a transition contributes to BOTH and the two
        // must agree on the command key by construction rather than by a second lookup.
        var commands = new List<(int? At, JsonObject Obj)>();

        if (app.Processes is { } processes)
        {
            var arr = new JsonArray();
            semantic.Add("/processes");

            for (var i = 0; i < processes.Count; i++)
            {
                var p = processes[i];
                var at = $"/processes/{i}";
                var o = new JsonObject();

                // The schema tells the author the key "defaults from the entity" and nothing implemented
                // that default, so an omitted key produced a document the gate rejected for a missing
                // required property — a promise made to the model and broken by the lowerer. An entity
                // has at most one lifecycle, so its key is the only sensible derivation.
                Put(at, "key", p.Key ?? (p.Entity is { } e ? $"{e}_flow" : null), o);
                Put(at, "entity", p.Entity, o);
                Put(at, "stateField", p.StateField, o);
                Put(at, "initialState", p.InitialState, o);

                if (p.States is { } states)
                {
                    var sarr = new JsonArray();
                    semantic.Add($"{at}/states");
                    for (var s = 0; s < states.Count; s++)
                    {
                        var sAt = $"{at}/states/{s}";
                        var so = new JsonObject();
                        Put(sAt, "key", states[s].Key, so);
                        Put(sAt, "label", states[s].Label, so);
                        Put(sAt, "color", states[s].Color, so);
                        Put(sAt, "terminal", states[s].Terminal, so);
                        Put(sAt, "phase", CordVocabulary.StatePhases.Lower(states[s].Phase), so);
                        Overlay(sAt, states[s].Raw, so);
                        sarr.Add(so);
                    }
                    o["states"] = sarr;
                }

                if (p.Transitions is { } transitions)
                {
                    var tarr = new JsonArray();
                    semantic.Add($"{at}/transitions");
                    for (var t = 0; t < transitions.Count; t++)
                    {
                        var tr = transitions[t];
                        var tAt = $"{at}/transitions/{t}";
                        var to = new JsonObject();

                        Put(tAt, "key", tr.Key, to);
                        Put(tAt, "label", tr.Label, to);
                        if (tr.From is { } from)
                        {
                            to["from"] = new JsonArray(from.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
                            semantic.Add($"{tAt}/from");
                        }
                        Put(tAt, "to", tr.To, to);
                        Put(tAt, "requiredFields", Strings(tr.RequiredFields), to);

                        // A transition WITH effects gets a command; one without does not, and the
                        // compiler's own synthesis fills the gap. That is the "emit absence" the plan
                        // asks for: the author says the record moves, not that a button exists.
                        if (Command(tr, p.Entity, tAt) is { } cmd)
                        {
                            to["command"] = cmd.Key;
                            semantic.Add($"{tAt}/command");
                            commands.Add((null, cmd.Obj));
                        }

                        Overlay(tAt, tr.Raw, to);
                        tarr.Add(to);
                    }
                    o["transitions"] = tarr;
                }

                Overlay(at, p.Raw, o);
                arr.Add(o);
            }
            doc["processes"] = arr;
        }

        // ---- standalone actions, restored to their recorded positions -------------------------------

        if (app.Actions is { } actions)
        {
            for (var i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                var at = $"/commands/{i}";   // provisional; rewritten below once the order is known
                var o = new JsonObject();

                Put(at, "key", a.Key, o);
                Put(at, "label", a.Label, o);
                Put(at, "entity", a.Entity, o);
                Put(at, "description", a.Description, o);
                Put(at, "icon", a.Icon, o);
                Put(at, "style", CordVocabulary.CommandStyles.Lower(a.Style), o);
                Put(at, "placements", Placements(a.Placements), o);
                Put(at, "successMessage", a.SuccessMessage, o);
                if (Confirm(a.Confirm, at, semantic, raw) is { } c) { o["confirm"] = c; }
                if (Ask(a.Ask, at, semantic, raw) is { } k) { o["input"] = k; }
                if (When(a.When, $"{at}/when", semantic, raw) is { } w)
                {
                    o["when"] = w;
                    semantic.Add($"{at}/when");
                }
                if (Effects(a.Effects, at, semantic, raw) is { } e) { o["effects"] = e; }
                Overlay(at, a.Raw, o);

                commands.Add((a.At, o));
            }
        }

        if (commands.Count > 0)
        {
            semantic.Add("/commands");
            // Transition-derived commands keep their relative order; each action is INSERTED at the
            // index it was imported from. Rebuilding without those indices reproduced 10 of 13 corpus
            // apps; with them, 13 of 13. Array order is meaningful to DefinitionHash.Canonical, so this
            // is not tidiness — it is the round-trip.
            var ordered = commands.Where(c => c.At is null).Select(c => c.Obj).ToList();
            foreach (var (at, obj) in commands.Where(c => c.At is not null).OrderBy(c => c.At))
                ordered.Insert(Math.Min(at!.Value, ordered.Count), obj);

            doc["commands"] = new JsonArray(ordered.Select(o => (JsonNode)o).ToArray());
        }

        // ---- workflows -------------------------------------------------------------------------------

        if (app.Schedules is { } schedules)
        {
            var arr = new JsonArray();
            semantic.Add("/workflows");
            for (var i = 0; i < schedules.Count; i++)
            {
                var s = schedules[i];
                var at = $"/workflows/{i}";
                var o = new JsonObject();

                Put(at, "key", s.Key, o);
                Put(at, "name", s.Name, o);
                if (s.Trigger is { } tr)
                {
                    var t = new JsonObject();
                    // "only when THIS field changed" is a distinct platform event. Lowering it as
                    // record.updated would be structurally legal and semantically wrong — the
                    // automation would fire on every write — which nothing would ever reject.
                    Put($"{at}/trigger", "event", CordVocabulary.TriggerEvent(tr.Event, tr.Field), t);
                    Put($"{at}/trigger", "entity", tr.Entity, t);
                    Put($"{at}/trigger", "field", tr.Field, t);
                    Put($"{at}/trigger", "cron", tr.Cron, t);
                    Overlay($"{at}/trigger", tr.Raw, t);
                    o["trigger"] = t;
                    semantic.Add($"{at}/trigger");
                }
                if (When(s.When, $"{at}/when", semantic, raw) is { } w)
                {
                    o["when"] = w;
                    semantic.Add($"{at}/when");
                }
                if (Effects(s.Effects, at, semantic, raw) is { } e) { o["effects"] = e; }
                Overlay(at, s.Raw, o);
                arr.Add(o);
            }
            doc["workflows"] = arr;
        }

        // ---- roles -----------------------------------------------------------------------------------

        if (app.Roles is { } roles)
        {
            var arr = new JsonArray();
            semantic.Add("/roles");
            for (var i = 0; i < roles.Count; i++)
            {
                var r = roles[i];
                var at = $"/roles/{i}";
                var o = new JsonObject();

                Put(at, "key", r.Key, o);
                Put(at, "name", r.Name, o);
                Put(at, "description", r.Description, o);

                if (r.Grants is { } grants)
                {
                    var garr = new JsonArray();
                    semantic.Add($"{at}/grants");
                    for (var g = 0; g < grants.Count; g++)
                    {
                        var gAt = $"{at}/grants/{g}";
                        var go = new JsonObject();
                        Put(gAt, "entity", grants[g].Entity, go);
                        Put(gAt, "create", grants[g].Create, go);
                        Put(gAt, "read", grants[g].Read, go);
                        Put(gAt, "update", grants[g].Update, go);
                        Put(gAt, "delete", grants[g].Delete, go);
                        Put(gAt, "commands", Strings(grants[g].Commands), go);
                        Overlay(gAt, grants[g].Raw, go);
                        garr.Add(go);
                    }
                    o["grants"] = garr;
                }

                Overlay(at, r.Raw, o);
                arr.Add(o);
            }
            doc["roles"] = arr;
        }

        // ---- the derivation that makes the slice worth doing -----------------------------------------

        (string Key, JsonObject Obj)? Command(CordTransition tr, string? entity, string tAt)
        {
            // No effects and nothing to present: the state change IS the whole story, and
            // AppCompiler.SynthesizeCommands will produce the button. Emitting an empty command here
            // would be authoring something nobody asked for.
            //
            // `When` is in this list for a different reason than the rest of it. The others are
            // presentation, and losing one costs an icon. A guard is the only thing here that can be
            // silently DROPPED into a security hole — a synthesized command is unconditional — so it
            // has to be a reason to emit a command, not merely something a command carries.
            if (tr.Effects is null && tr.Confirm is null && tr.Ask is null && tr.When is null
                && tr.Icon is null && tr.Style is null && tr.Placements is null
                && tr.SuccessMessage is null && tr.CommandKey is null && tr.CommandLabel is null)
                return null;

            // The key defaults to the transition's own — which is what a Cord-authored app gets, and
            // what 16 of 52 corpus commands already used. CommandKey is set by the importer only where
            // a stored document chose differently.
            var key = tr.CommandKey ?? tr.Key;
            if (key is null) return null;

            var at = $"{tAt}/command";
            var o = new JsonObject { ["key"] = key };
            var label = tr.CommandLabel ?? tr.Label;
            if (label is not null) o["label"] = label;
            if (entity is not null) o["entity"] = entity;
            if (tr.Icon is not null) o["icon"] = tr.Icon;
            if (CordVocabulary.CommandStyles.Lower(tr.Style) is { } style) o["style"] = style;
            if (Placements(tr.Placements) is { } pl) o["placements"] = pl;
            if (tr.SuccessMessage is not null) o["successMessage"] = tr.SuccessMessage;
            if (Confirm(tr.Confirm, at, semantic, raw) is { } c) o["confirm"] = c;
            if (Ask(tr.Ask, at, semantic, raw) is { } k) o["input"] = k;
            if (When(tr.When, $"{at}/when", semantic, raw) is { } g)
            {
                o["when"] = g;
                semantic.Add($"{at}/when");
            }

            // ALWAYS an array, EMPTY when the move has nothing to do besides change the state.
            //
            // A transition that carries only presentation — an icon, a style, a confirmation — still
            // needs a command to hang them on, and the App Definition permits a command with no effects
            // precisely when a transition binds it: the state change IS the effect. But `effects` is a
            // REQUIRED property, so omitting it produced "Required properties [effects] are not
            // present" on a document that was otherwise fine (smoke run 2026-08-11, /commands/6).
            // Suppressing the command instead would have been the other wrong answer — it would throw
            // away the icon and the style the author asked for.
            o["effects"] = Effects(tr.Effects, at, semantic, raw) ?? new JsonArray();

            return (key, o);
        }
    }

    /// <summary>Placements, each through the one map — see <see cref="CordVocabulary.Placements"/>.
    /// Cord's <c>rowMenu</c> is the platform's <c>tableRow</c>, and a word Cord does not know is passed
    /// through so the gate names it rather than Cord quietly substituting something plausible.</summary>
    private static JsonArray? Placements(IReadOnlyList<string>? values) =>
        Strings(values?.Select(v => CordVocabulary.Placements.Lower(v)!).ToList());

    private static JsonArray? Strings(IReadOnlyList<string>? values) =>
        values is null ? null : new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static JsonObject? Confirm(CordConfirm? c, string at, List<string> semantic, List<string> raw)
    {
        if (c is null) return null;
        var o = new JsonObject();
        if (c.Title is not null) o["title"] = c.Title;
        if (c.Message is not null) o["message"] = c.Message;
        if (c.ConfirmLabel is not null) o["confirmLabel"] = c.ConfirmLabel;
        if (c.Tone is not null) o["tone"] = c.Tone;
        semantic.Add($"{at}/confirm");
        if (c.Raw is { } r)
        {
            foreach (var (key, _) in r) raw.Add(CordJson.Pointer($"{at}/confirm", key));
            CordJson.Merge(o, r);
        }
        return o;
    }

    private static JsonObject? Ask(CordAsk? a, string at, List<string> semantic, List<string> raw)
    {
        if (a is null) return null;
        var o = new JsonObject();
        if (Strings(a.Fields) is { } f) o["fields"] = f;
        if (Strings(a.Required) is { } r) o["required"] = r;
        semantic.Add($"{at}/input");
        if (a.Raw is { } rr)
        {
            foreach (var (key, _) in rr) raw.Add(CordJson.Pointer($"{at}/input", key));
            CordJson.Merge(o, rr);
        }
        return o;
    }

    private static JsonObject? When(CordWhen? w, string at, List<string> semantic, List<string> raw)
    {
        if (w is null) return null;
        var o = new JsonObject();
        if (w.Field is not null) o["field"] = w.Field;
        if (w.Operator is not null) o["operator"] = w.Operator;
        if (w.Value is not null) o["value"] = w.Value.DeepClone();
        if (w.All is { } all)
        {
            var arr = new JsonArray();
            for (var i = 0; i < all.Count; i++)
                if (When(all[i], $"{at}/all/{i}", semantic, raw) is { } sub) arr.Add(sub);
            o["all"] = arr;
        }
        if (w.Raw is { } r)
        {
            foreach (var (key, _) in r) raw.Add(CordJson.Pointer(at, key));
            CordJson.Merge(o, r);
        }
        return o;
    }

    private static JsonArray? Effects(IReadOnlyList<CordEffect>? effects, string at,
        List<string> semantic, List<string> raw)
    {
        if (effects is null) return null;
        var arr = new JsonArray();
        semantic.Add($"{at}/effects");

        for (var i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            var o = new JsonObject();
            if (e.Type is not null) o["type"] = e.Type;
            if (e.Target is not null) o["target"] = e.Target.DeepClone();
            if (e.Set is not null) o["set"] = e.Set.DeepClone();
            if (e.SetIfEmpty is { } s) o["setIfEmpty"] = s;
            if (e.Entity is not null) o["entity"] = e.Entity;
            if (e.Source is not null) o["source"] = e.Source;
            if (e.Key is not null) o["key"] = e.Key;
            if (e.To is not null) o["to"] = e.To;
            if (e.Title is not null) o["title"] = e.Title;
            if (e.Message is not null) o["message"] = e.Message;
            if (e.Link is not null) o["link"] = e.Link;
            if (e.Raw is { } r)
            {
                foreach (var (key, _) in r) raw.Add(CordJson.Pointer($"{at}/effects/{i}", key));
                CordJson.Merge(o, r);
            }
            arr.Add(o);
        }
        return arr;
    }
}
