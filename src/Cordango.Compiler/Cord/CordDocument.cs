// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// A <see cref="CordApp"/> written back out as the operations that would create it.
///
/// <para><b>What this is for.</b> Cord could always read an application (<see cref="CordImport"/>) and
/// write one (<see cref="CordLower"/>), but the semantic model in between existed only in memory. It
/// could be DESCRIBED — <see cref="CordInspect"/> renders prose for a model to read — and prose is not
/// a source format. This is the missing third side: the model as a document, in the same vocabulary the
/// author writes in, which is what a <c>.cord</c> file is a syntax over.</para>
///
/// <para><b>The document IS an operation batch</b>, and that is the design decision worth arguing with.
/// The alternative was a separate declarative shape — a whole second vocabulary to specify, validate
/// and keep in step. Instead a document is the ops that would build the app from nothing, so there is
/// one vocabulary for authoring and for storage, the reader is <see cref="CordTransaction.Prepare"/>
/// which already exists and is already tested, and anything a document can say is by construction
/// something a model can say. A typed <c>.cord.ts</c> later is a nicer way to spell these ops, not a
/// different thing.</para>
///
/// <para><b>What it cannot write, it says so.</b> Three kinds of information live in an imported model
/// and have no operation: <c>Raw</c> fragments (the importer's escape hatch, deliberately absent from
/// the authoring surface — plan risk 3), array-position bookkeeping such as
/// <see cref="CordAction.At"/>, and a transition's <see cref="CordTransition.CommandKey"/> where a
/// stored document named its command something other than the transition. Every one of those is
/// reported by pointer in <see cref="CordWritten.Unwritable"/> rather than dropped quietly. An empty
/// list is the real claim: this app is fully expressible as semantic operations.</para>
/// </summary>
public static class CordDocument
{
    /// <summary>The app as <c>{key, name, version, ops:[…]}</c>, plus what could not be written.</summary>
    public static CordWritten Write(CordApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var unwritable = new List<string>();
        var ops = new JsonArray();

        if (app.Raw is not null) unwritable.Add("/");

        foreach (var (entity, i) in app.EntityList.Select((e, i) => (e, i)))
            ops.Add(Op("upsert_entity", "entity", Entity(entity, $"/entities/{i}", unwritable)));

        foreach (var (process, i) in (app.Processes ?? []).Select((p, i) => (p, i)))
            ops.Add(Op("upsert_lifecycle", "lifecycle", Process(process, $"/processes/{i}", unwritable)));

        foreach (var (action, i) in (app.Actions ?? []).Select((a, i) => (a, i)))
        {
            var at = $"/commands/{i}";
            // Position in the lowered `commands[]` array. Cord-authored actions append, so this is null
            // on anything Cord wrote and set only where an import found standalone commands INTERLEAVED
            // among transition-derived ones — an order nothing derives. Writing the document loses it,
            // and losing it changes DefinitionHash, so it is named rather than shrugged at.
            if (action.At is not null) unwritable.Add($"{at}/at");
            ops.Add(Op("upsert_action", "action", Action(action, at, unwritable)));
        }

        foreach (var (schedule, i) in (app.Schedules ?? []).Select((s, i) => (s, i)))
            ops.Add(Op("upsert_automation", "automation", Schedule(schedule, $"/workflows/{i}", unwritable)));

        foreach (var (role, i) in (app.Roles ?? []).Select((r, i) => (r, i)))
            ops.Add(Op("upsert_role", "role", Role(role, $"/roles/{i}", unwritable)));

        foreach (var (screen, i) in (app.Screens ?? []).Select((s, i) => (s, i)))
            ops.Add(Op("upsert_screen", "screen", Screen(screen, $"/screens/{i}", unwritable)));

        var doc = new JsonObject { ["key"] = app.Key, ["name"] = app.Name, ["version"] = app.Version };
        doc["ops"] = ops;
        return new CordWritten(doc, unwritable);
    }

    private static JsonObject Op(string name, string slot, JsonObject body) =>
        new() { ["op"] = name, [slot] = body };

    // ---- domain -----------------------------------------------------------------------------------

    private static JsonObject Entity(CordEntity e, string at, List<string> unwritable)
    {
        Raw(e.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "key", e.Key);
        Set(o, "label", e.Label);
        Set(o, "labelPlural", e.LabelPlural);
        Set(o, "description", e.Description);
        Set(o, "icon", e.Icon);
        Set(o, "displayField", e.DisplayField);
        Set(o, "kind", e.Kind);

        // `via` never goes back on the wire — it is derived on the way down, and writing it would hand
        // an author back the decision the shape exists to remove. Same for an aggregate's join below.
        if (e.Owner is { } owner) o["ownedBy"] = new JsonObject { ["parent"] = owner.Parent };
        if (e.Series is { } series)
            o["series"] = new JsonObject { ["partition"] = series.Partition, ["order"] = series.Order };

        if (e.FieldList.Count > 0)
        {
            var fields = new JsonArray();
            foreach (var (f, i) in e.FieldList.Select((f, i) => (f, i)))
                fields.Add(Field(f, $"{at}/fields/{i}", unwritable));
            o["fields"] = fields;
        }

        if (e.Unique is { Count: > 0 } unique)
        {
            var groups = new JsonArray();
            foreach (var group in unique) groups.Add(new JsonArray([.. group.Select(k => (JsonNode)k!)]));
            o["unique"] = groups;
        }

        return o;
    }

    private static JsonObject Field(CordField f, string at, List<string> unwritable)
    {
        Raw(f.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "key", f.Key);
        Set(o, "label", f.Label);
        Set(o, "type", f.Type);
        Set(o, "required", f.Required);
        Set(o, "unique", f.Unique);
        Set(o, "indexed", f.Indexed);
        Set(o, "help", f.Help);
        Set(o, "group", f.Group);
        if (f.Default is { } dflt) o["default"] = dflt.DeepClone();
        Set(o, "unit", f.Unit);
        Set(o, "currency", f.Currency);
        Set(o, "role", f.Role);
        Set(o, "target", f.TargetEntity);     // `target`, not `targetEntity` — CordWire's spelling.
        Set(o, "targetApp", f.TargetApp);
        Set(o, "onDelete", f.OnDelete);

        if (f.Options is { Count: > 0 } options)
        {
            var arr = new JsonArray();
            foreach (var opt in options)
            {
                var x = new JsonObject { ["value"] = opt.Value, ["label"] = opt.Label };
                Set(x, "color", opt.Color);
                Set(x, "phase", opt.Phase);
                arr.Add(x);
            }
            o["options"] = arr;
        }

        switch (f.Calc)
        {
            case CordExpr expr:
                o["expr"] = expr.Expr;    // Sits on the field, not under `computed`.
                break;
            case CordAggregate agg:
                o["aggregate"] = Aggregate(agg, $"{at}/aggregate", unwritable);
                break;
        }

        return o;
    }

    private static JsonObject Aggregate(CordAggregate a, string at, List<string> unwritable)
    {
        var o = new JsonObject { ["op"] = a.Op, ["of"] = a.Of };
        Set(o, "field", a.Field);
        if (a.Over != CordAggregate.Mine) o["over"] = a.Over;

        // Via is DERIVED, so leaving it out is normally not a loss — 45 of 45 corpus aggregates infer
        // exactly. It is non-null only where the IMPORTER found that inference would not reproduce the
        // stored join, so a non-null Via is precisely the ambiguous case, and writing a document that
        // silently cannot be read back is the failure this line prevents.
        if (a.Via is not null) unwritable.Add($"{at}/via");

        o["during"] = a.During switch
        {
            CordCovering c => Window("covering", new JsonObject
            {
                ["from"] = c.From, ["to"] = c.To, ["myPoint"] = c.MyPoint,
            }),
            CordInside i => Window("inside", new JsonObject
            {
                ["at"] = i.At, ["myFrom"] = i.MyFrom, ["myTo"] = i.MyTo,
            }),
            _ => null,
        };
        if (o["during"] is null) o.Remove("during");

        if (a.Where is { Count: > 0 } where)
        {
            var arr = new JsonArray();
            foreach (var (w, i) in where.Select((w, i) => (w, i)))
            {
                Raw(w.Raw, $"{at}/where/{i}", unwritable);
                var x = new JsonObject();
                Set(x, "field", w.Field);
                Set(x, "operator", w.Operator);
                if (w.Value is { } v) x["value"] = v.DeepClone();
                arr.Add(x);
            }
            o["where"] = arr;
        }

        return o;
    }

    private static JsonObject Window(string variant, JsonObject body)
    {
        foreach (var key in body.Where(p => p.Value is null).Select(p => p.Key).ToList()) body.Remove(key);
        return new JsonObject { [variant] = body };
    }

    // ---- behaviour --------------------------------------------------------------------------------

    private static JsonObject Process(CordProcess p, string at, List<string> unwritable)
    {
        Raw(p.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "entity", p.Entity);
        Set(o, "stateField", p.StateField);
        Set(o, "initialState", p.InitialState);
        // The key defaults from the entity, and upsert_lifecycle accepts an explicit one — so a stored
        // document that chose differently is written, not reported. (It was reported here first, which
        // is what a measurement is for: 14 of the 15 corpus apps were failing on a property the
        // authoring surface already had.)
        if (p.Key is not null && p.Key != p.Entity) o["key"] = p.Key;

        if (p.States is { Count: > 0 } states)
        {
            var arr = new JsonArray();
            foreach (var s in states)
            {
                var x = new JsonObject { ["key"] = s.Key, ["label"] = s.Label };
                Set(x, "color", s.Color);
                Set(x, "terminal", s.Terminal);
                Word(x, "phase", s.Phase, CordVocabulary.StatePhases, $"{at}/states/{s.Key}/phase", unwritable);
                arr.Add(x);
            }
            o["states"] = arr;
        }

        if (p.Transitions is { Count: > 0 } transitions)
        {
            var arr = new JsonArray();
            foreach (var (t, i) in transitions.Select((t, i) => (t, i)))
                arr.Add(Transition(t, $"{at}/transitions/{i}", unwritable));
            o["transitions"] = arr;
        }

        return o;
    }

    private static JsonObject Transition(CordTransition t, string at, List<string> unwritable)
    {
        Raw(t.Raw, at, unwritable);

        // The command a transition lowers to is derived from the transition itself. Where a stored
        // document disagreed, the importer recorded the difference — and there is no operation that can
        // say it, because asking an author to name a command key is asking them to maintain a join.
        if (t.CommandKey is not null) unwritable.Add($"{at}/command/key");
        if (t.CommandLabel is not null) unwritable.Add($"{at}/command/label");

        var o = new JsonObject();
        Set(o, "key", t.Key);
        Set(o, "label", t.Label);
        if (t.From is { Count: > 0 } from) o["from"] = new JsonArray([.. from.Select(s => (JsonNode)s!)]);
        Set(o, "to", t.To);
        if (t.RequiredFields is { Count: > 0 } required)
            o["requiredFields"] = new JsonArray([.. required.Select(s => (JsonNode)s!)]);
        Set(o, "icon", t.Icon);
        Word(o, "style", t.Style, CordVocabulary.CommandStyles, $"{at}/style", unwritable);
        Placements(o, t.Placements, at, unwritable);
        Set(o, "successMessage", t.SuccessMessage);
        if (Confirm(t.Confirm, $"{at}/confirm", unwritable) is { } c) o["confirm"] = c;
        if (Ask(t.Ask, $"{at}/ask", unwritable) is { } a) o["ask"] = a;
        if (When(t.When, $"{at}/when", unwritable) is { } w) o["when"] = w;
        if (Effects(t.Effects, $"{at}/effects", unwritable) is { } e) o["effects"] = e;
        return o;
    }

    private static JsonObject Action(CordAction a, string at, List<string> unwritable)
    {
        Raw(a.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "key", a.Key);
        Set(o, "label", a.Label);
        Set(o, "entity", a.Entity);
        Set(o, "description", a.Description);
        Set(o, "icon", a.Icon);
        Word(o, "style", a.Style, CordVocabulary.CommandStyles, $"{at}/style", unwritable);
        Placements(o, a.Placements, at, unwritable);
        Set(o, "successMessage", a.SuccessMessage);
        if (Confirm(a.Confirm, $"{at}/confirm", unwritable) is { } c) o["confirm"] = c;
        if (Ask(a.Ask, $"{at}/input", unwritable) is { } k) o["ask"] = k;
        if (When(a.When, $"{at}/when", unwritable) is { } w) o["when"] = w;
        if (Effects(a.Effects, $"{at}/effects", unwritable) is { } e) o["effects"] = e;
        return o;
    }

    private static JsonObject Schedule(CordSchedule s, string at, List<string> unwritable)
    {
        Raw(s.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "key", s.Key);
        Set(o, "name", s.Name);
        // Flattened: `on` + `entity` + optional `field`/`cron`, never a nested trigger object.
        if (s.Trigger is { } trigger)
        {
            // `field.changed` is the one trigger no word map can raise, because it is not a word — it
            // is what `record.updated` lowers to WHEN A FIELD IS NAMED. The inverse therefore depends
            // on two properties at once, which is exactly the shape a Word/Lowered pair cannot hold,
            // so it is spelled out here rather than bent into the map. Eight corpus automations are
            // this, and every one of them was unwritable until it was.
            if (trigger.Event == CordVocabulary.FieldChanged) o["on"] = CordVocabulary.RecordUpdated;
            else Word(o, "on", trigger.Event, CordVocabulary.TriggerEvents, $"{at}/on", unwritable);
            Set(o, "entity", trigger.Entity);
            Set(o, "field", trigger.Field);
            Set(o, "cron", trigger.Cron);
        }
        if (When(s.When, $"{at}/when", unwritable) is { } w) o["when"] = w;
        if (Effects(s.Effects, $"{at}/effects", unwritable) is { } e) o["effects"] = e;
        return o;
    }

    private static JsonObject Role(CordRole r, string at, List<string> unwritable)
    {
        Raw(r.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "key", r.Key);
        Set(o, "name", r.Name);
        Set(o, "description", r.Description);

        if (r.Grants is { Count: > 0 } grants)
        {
            var arr = new JsonArray();
            foreach (var (g, i) in grants.Select((g, i) => (g, i)))
            {
                Raw(g.Raw, $"{at}/grants/{i}", unwritable);
                var x = new JsonObject();
                Set(x, "entity", g.Entity);
                Set(x, "create", g.Create);
                Set(x, "read", g.Read);
                Set(x, "update", g.Update);
                Set(x, "delete", g.Delete);
                if (g.Commands is { Count: > 0 } cmds)
                    x["commands"] = new JsonArray([.. cmds.Select(s => (JsonNode)s!)]);
                arr.Add(x);
            }
            o["grants"] = arr;
        }

        return o;
    }

    private static JsonObject? Confirm(CordConfirm? c, string at, List<string> unwritable)
    {
        if (c is null) return null;
        Raw(c.Raw, at, unwritable);
        var o = new JsonObject();
        Set(o, "title", c.Title);
        Set(o, "message", c.Message);
        Set(o, "confirmLabel", c.ConfirmLabel);
        Set(o, "tone", c.Tone);
        return o;
    }

    private static JsonObject? Ask(CordAsk? a, string at, List<string> unwritable)
    {
        if (a is null) return null;
        Raw(a.Raw, at, unwritable);
        var o = new JsonObject();
        if (a.Fields is { Count: > 0 } f) o["fields"] = new JsonArray([.. f.Select(s => (JsonNode)s!)]);
        if (a.Required is { Count: > 0 } r) o["required"] = new JsonArray([.. r.Select(s => (JsonNode)s!)]);
        return o;
    }

    private static JsonObject? When(CordWhen? w, string at, List<string> unwritable)
    {
        if (w is null) return null;
        Raw(w.Raw, at, unwritable);

        var o = new JsonObject();
        Set(o, "field", w.Field);
        Word(o, "operator", w.Operator, CordVocabulary.ConditionOperators, $"{at}/operator", unwritable);
        if (w.Value is { } v) o["value"] = v.DeepClone();
        if (w.All is { Count: > 0 } all)
        {
            var arr = new JsonArray();
            foreach (var (sub, i) in all.Select((s, i) => (s, i)))
                if (When(sub, $"{at}/all/{i}", unwritable) is { } x) arr.Add(x);
            o["all"] = arr;
        }
        return o;
    }

    private static JsonArray? Effects(IReadOnlyList<CordEffect>? effects, string at, List<string> unwritable)
    {
        if (effects is null) return null;

        var arr = new JsonArray();
        foreach (var (e, i) in effects.Select((e, i) => (e, i)))
        {
            Raw(e.Raw, $"{at}/{i}", unwritable);
            var o = new JsonObject();
            Word(o, "type", e.Type, CordVocabulary.EffectTypes, $"{at}/{i}/type", unwritable);
            if (e.Target is { } target) o["target"] = target.DeepClone();
            if (e.Set is { } set) o["set"] = (JsonObject)set.DeepClone();
            Set(o, "setIfEmpty", e.SetIfEmpty);
            Set(o, "entity", e.Entity);
            Set(o, "source", e.Source);
            Set(o, "key", e.Key);
            Set(o, "to", e.To);
            Set(o, "title", e.Title);
            Set(o, "message", e.Message);
            Set(o, "link", e.Link);
            arr.Add(o);
        }
        return arr;
    }

    // ---- screens ----------------------------------------------------------------------------------

    private static JsonObject Screen(CordScreen s, string at, List<string> unwritable)
    {
        // No Raw on a screen or a section. Import only claims a page when its complete lowered shape
        // can be raised back into these semantics; anything else remains in the app overlay. That
        // keeps an operation file honest: every screen written here is independently editable.
        var o = new JsonObject();
        Set(o, "key", s.Key);
        Set(o, "label", s.Label);
        Set(o, "subject", s.Subject);

        if (s.Sections is { Count: > 0 } sections)
        {
            var arr = new JsonArray();
            foreach (var (sec, i) in sections.Select((x, i) => (x, i)))
                arr.Add(Section(sec, $"{at}/sections/{i}", unwritable));
            o["sections"] = arr;
        }

        if (s.Tabs is { Count: > 0 } tabs)
        {
            var arr = new JsonArray();
            foreach (var (tab, i) in tabs.Select((x, i) => (x, i)))
            {
                var t = new JsonObject { ["key"] = tab.Key };
                Set(t, "label", tab.Label);
                if (tab.Sections is { Count: > 0 } inner)
                {
                    var children = new JsonArray();
                    foreach (var (sec, j) in inner.Select((x, j) => (x, j)))
                        children.Add(Section(sec, $"{at}/tabs/{i}/sections/{j}", unwritable));
                    t["sections"] = children;
                }
                arr.Add(t);
            }
            o["tabs"] = arr;
        }

        return o;
    }

    private static JsonObject Section(CordSection s, string at, List<string> unwritable)
    {
        var o = new JsonObject();
        Set(o, "key", s.Key);
        Set(o, "kind", s.Kind);
        Set(o, "of", s.Of);
        Set(o, "label", s.Label);
        Word(o, "view", s.View, CordVocabulary.Views, $"{at}/view", unwritable);
        if (s.Filter is { Count: > 0 } filter)
        {
            var arr = new JsonArray();
            foreach (var (f, i) in filter.Select((f, i) => (f, i)))
                if (When(f, $"{at}/filter/{i}", unwritable) is { } x) arr.Add(x);
            o["filter"] = arr;
        }
        if (s.Sort is { Count: > 0 } sort)
        {
            var arr = new JsonArray();
            foreach (var so in sort)
            {
                var x = new JsonObject { ["field"] = so.Field };
                Set(x, "direction", so.Direction);
                arr.Add(x);
            }
            o["sort"] = arr;
        }
        if (s.Columns is { Count: > 0 } columns)
            o["columns"] = new JsonArray([.. columns.Select(c => (JsonNode)c!)]);
        Set(o, "dateField", s.DateField);
        Set(o, "groupBy", s.GroupBy);
        if (s.Value is { } m)
        {
            var x = new JsonObject { ["op"] = m.Op };
            Set(x, "field", m.Field);
            Set(x, "groupBy", m.GroupBy);
            o["value"] = x;
        }
        Set(o, "text", s.Text);
        Word(o, "visual", s.Visual == CordVocabulary.AutoVisual ? null : s.Visual,
            CordVocabulary.ChartVisuals, $"{at}/visual", unwritable);

        // A split's two sides go through this same function, one level down — the wire reads them that
        // way and the lowerer lowers them that way, so writing them any differently would be a third
        // opinion about what a section is.
        if (s.Sections is { Count: > 0 } sides)
        {
            var arr = new JsonArray();
            foreach (var (side, i) in sides.Select((x, i) => (x, i)))
                arr.Add(Section(side, $"{at}/sections/{i}", unwritable));
            o["sections"] = arr;
        }
        if (s.Ratio is { Count: > 0 } ratio)
            o["ratio"] = new JsonArray([.. ratio.Select(x => (JsonNode)x)]);

        return o;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static void Set(JsonObject o, string key, string? value)
    {
        if (value is not null) o[key] = value;
    }

    private static void Set(JsonObject o, string key, bool? value)
    {
        if (value is not null) o[key] = value.Value;
    }

    /// <summary>
    /// A closed-vocabulary value, raised — or reported.
    ///
    /// <para><b>Never fall back to the platform's own word.</b> That reads like tolerance and is the
    /// opposite: the operation schema constrains these with an enum, so passing an unraisable value
    /// through produces a document Cord wrote and Cord refuses. Four of these existed on the first
    /// draft of this file and the corpus caught all four at once — a trigger, an operator, an effect
    /// type and a wildcard grant.</para>
    /// </summary>
    private static void Word(JsonObject o, string key, string? value, CordWordMap map, string at,
        List<string> unwritable)
    {
        if (value is null) return;
        if (map.Raise(value) is { } word) o[key] = word;
        else unwritable.Add(at);
    }

    private static void Placements(JsonObject o, IReadOnlyList<string>? placements, string at,
        List<string> unwritable)
    {
        if (placements is not { Count: > 0 }) return;

        var arr = new JsonArray();
        foreach (var (p, i) in placements.Select((p, i) => (p, i)))
        {
            if (CordVocabulary.Placements.Raise(p) is { } word) arr.Add(word);
            else unwritable.Add($"{at}/placements/{i}");
        }
        if (arr.Count > 0) o["placements"] = arr;
    }

    /// <summary>An escape hatch used is a thing no operation can say. Recording the pointer is the
    /// whole contract: a caller that sees an empty list knows the document is complete.</summary>
    private static void Raw(JsonObject? raw, string at, List<string> unwritable)
    {
        if (raw is not null) unwritable.Add($"{at}/raw");
    }
}

/// <param name="Json">The document: <c>{key, name, version, ops:[…]}</c>.</param>
/// <param name="Unwritable">Pointers into the MODEL naming what no operation can express. Empty means
/// the app is fully expressible as semantic operations — which is the claim a <c>.cord</c> file has to
/// be able to make before it can be a source format rather than a view.</param>
public sealed record CordWritten(JsonObject Json, IReadOnlyList<string> Unwritable)
{
    public bool Complete => Unwritable.Count == 0;
}
