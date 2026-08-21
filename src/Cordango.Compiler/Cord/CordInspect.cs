// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The draft, described rather than serialized.
///
/// <para>An author evolving an application needs to know what is already there. Handing over the App
/// Definition to answer that is the problem this layer exists to remove — six thousand lines to learn
/// that <c>period</c> has a <c>scenario</c> reference. So inspection returns prose: an outline at the
/// root, and one entity in full when asked for it.</para>
///
/// <para>Written as TEXT, not JSON. It is read, never parsed — by a model that reads English better
/// than it reads a schema, and by a person reading a transcript trying to work out what the model
/// knew. A JSON reply would be longer and would invite treating it as a contract.</para>
/// </summary>
public static class CordInspect
{
    /// <param name="path">Empty or <c>/</c> for the outline; <c>/entities/period</c> for one entity.</param>
    public static string Describe(CordApp app, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var trimmed = (path ?? "/").Trim().Trim('/');
        if (trimmed.Length == 0) return Outline(app);

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["entities", var key, ..])
            return app.EntityList.FirstOrDefault(e => e.Key == key) is { } entity
                ? Entity(entity, app)
                : $"There is no entity '{key}'. The app has: {Names(app)}.";

        if (segments is ["screens", var screenKey, ..])
            return Screen(app, screenKey);

        return $"'{path}' is not something to inspect. Use '/' for the whole app, "
             + "'/entities/<key>' for one entity, or '/screens/<key>' for one screen.";
    }

    /// <summary>
    /// One screen in full — from the model when it was raised, and from the PRESERVED definition when
    /// it was not.
    ///
    /// <para>The second half is the point. An imported app whose pages cannot be expressed as
    /// operations keeps them verbatim in the raw remainder, and until now nothing could read them:
    /// the outline showed no screens, this tool had no path for one, and the review phase — whose
    /// whole job is revising a screen — ran with no idea what the screen contained. Describing the
    /// preserved form does not make it editable, and says so; it makes the conversation about it
    /// possible.</para>
    /// </summary>
    private static string Screen(CordApp app, string key)
    {
        if (app.Screens?.FirstOrDefault(s => s.Key == key) is { } modelled)
            return Modelled(modelled);

        if (RawPage(app, key) is { } page)
            return Preserved(page, app.Raw?["views"] as JsonArray, RefusalOf(app));

        var known = ScreenNames(app);
        return known.Length == 0
            ? "This app has no screens."
            : $"There is no screen '{key}'. The app has: {known}.";
    }

    private static string Outline(CordApp app)
    {
        var sb = new StringBuilder();
        sb.AppendLine(app.Name ?? app.Key ?? "(unnamed app)");

        if (app.EntityList.Count == 0)
        {
            sb.AppendLine().AppendLine("No entities yet.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine().AppendLine("Entities:");
        foreach (var e in app.EntityList)
        {
            var notes = new List<string>();
            if (e.Owner is { } owner) notes.Add($"inside {owner.Parent}");
            if (e.Series is not null) notes.Add("ordered series");
            if (e.Kind is "config" or "settings") notes.Add(e.Kind);

            var computed = e.FieldList.Count(f => f.Calc is not null);
            if (computed > 0) notes.Add($"{computed} calculated");

            sb.Append("- ").Append(e.Key)
              .Append(" (").Append(e.FieldList.Count).Append(" fields");
            if (notes.Count > 0) sb.Append(", ").Append(string.Join(", ", notes));
            sb.AppendLine(")");
        }

        // BEHAVIOUR, and it was missing until 2026-08-10 — a resumed session could see its entities and
        // screens but had no way to check whether the app already had a lifecycle, so its only options
        // were to re-author blindly or leave it out. Both are worse than looking.
        if (app.Processes is { Count: > 0 } processes)
        {
            sb.AppendLine().AppendLine("Lifecycles:");
            foreach (var p in processes)
            {
                sb.AppendLine($"- {p.Entity} on '{p.StateField}'"
                              + (p.InitialState is { } init ? $", starts {init}" : "")
                              + $" ({string.Join(" / ", p.StateList.Select(s => s.Key))})");
                // THE BUTTON KEY, per transition — the fact an author needs to put a state change on
                // a card or a row, and one that took two attempts to get right. It was printed, then
                // removed when it turned out the gate rejected every reference to it (synthesis ran
                // in the compiler, after validation), then restored once `ProcessCommands` taught the
                // gate the same naming rule the compiler uses. Printing a key that does not resolve
                // is worse than printing nothing, so this line is only correct while that holds —
                // `SynthesizedCommandGateTests` is what holds it.
                //
                // It belongs here rather than in the tool schema because it is app-specific
                // (`task_complete`, not a rule to memorise) and the schema is paid for on every model
                // call. Nothing needs authoring for the RECORD PAGE either way: synthesis stamps
                // `placements: [recordHeader]`, so the header renders these by itself.
                foreach (var t in p.TransitionList)
                    sb.AppendLine($"    {t.Key}: {string.Join(", ", t.FromList)} -> {t.To}"
                                  + $", button {t.CommandKey ?? $"{p.Entity}_{t.Key}"}"
                                  + (t.EffectList.Count > 0 ? $", {t.EffectList.Count} effect(s)" : ""));
            }
        }

        if (app.Actions is { Count: > 0 } actions)
        {
            sb.AppendLine().AppendLine("Actions:");
            foreach (var a in actions)
                sb.AppendLine($"- {a.Key} on {a.Entity}"
                              + (a.EffectList.Count > 0 ? $" ({a.EffectList.Count} effect(s))" : ""));
        }

        if (app.Schedules is { Count: > 0 } schedules)
        {
            sb.AppendLine().AppendLine("Automations:");
            foreach (var s in schedules)
                sb.AppendLine($"- {s.Key}: on {s.Trigger?.Event ?? "?"}"
                              + (s.Trigger?.Entity is { } e ? $" of {e}" : "")
                              + (s.When is not null ? ", conditional" : ""));
        }

        if (app.Roles is { Count: > 0 } roles)
        {
            sb.AppendLine().AppendLine("Roles:");
            foreach (var r in roles)
            {
                // Grants listed per entity WITH the commands, because "who can approve" is the question
                // a resumed model most needs answered and the one a role name alone never settles.
                sb.AppendLine($"- {r.Key} ({r.Name})");
                foreach (var g in r.GrantList)
                    sb.AppendLine($"    {g.Entity}: "
                                  + string.Join("", new[]
                                  {
                                      g.Create == true ? "c" : "-", g.Read == true ? "r" : "-",
                                      g.Update == true ? "u" : "-", g.Delete == true ? "d" : "-",
                                  })
                                  + (g.Commands is { Count: > 0 } cmds
                                      ? $" may {string.Join(", ", cmds)}"
                                      : ""));
            }
        }

        if (app.Screens is { Count: > 0 } screens)
        {
            sb.AppendLine().AppendLine("Screens:");
            foreach (var s in screens)
            {
                sb.AppendLine($"- {s.Label ?? s.Key}"
                              + (s.Subject is { } subject ? $" (about {subject})" : ""));
                // Sections listed with THEIR entity, not the screen's: a reader has to be able to see
                // that one screen draws on several, because that is the thing the old shape could not
                // express and the thing a reviewer most needs to check.
                foreach (var section in s.SectionList)
                    sb.AppendLine($"    {section.Kind}"
                                  + (section.Of is { } of ? $" of {of}" : "")
                                  + (section.Label is { } l ? $" — {l}" : "")
                                  + (section.Filter is { Count: > 0 } f ? $" (filtered on {f.Count})" : ""));
            }
        }
        else if (app.Raw?["pages"] is JsonArray raw && raw.Count > 0)
        {
            // The screens exist; they just are not operations. Saying "Screens:" and listing nothing
            // would be the same silence that made a model conclude the app had none.
            sb.AppendLine().AppendLine("Screens (preserved from the imported app — describable, but "
                                     + "not yet editable as operations):");
            foreach (var page in raw.OfType<JsonObject>())
                sb.AppendLine($"- {Text(page, "label") ?? Text(page, "key")}"
                              + (Text(page, "key") is { } k ? $" [{k}]" : "")
                              + (Text(page, "entity") is { } e ? $" (about {e})" : ""));
            sb.AppendLine($"  Ask for '/screens/<key>' to read one in full.");
            if (RefusalOf(app) is { } why)
                sb.AppendLine($"  Cord could not raise them into operations because {why}.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Entity(CordEntity e, CordApp app)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.Key ?? "(unkeyed)");

        if (e.Owner is { } owner)
            sb.AppendLine().AppendLine($"inside: {owner.Parent} (via {owner.Via})");
        if (e.Series is { } series)
            sb.AppendLine().AppendLine($"series: partitioned by {series.Partition}, ordered by {series.Order}");

        var typed = e.FieldList.Where(f => f.Calc is null).ToList();
        if (typed.Count > 0)
        {
            sb.AppendLine().AppendLine("fields:");
            foreach (var f in typed)
            {
                sb.Append("  ").Append(f.Key).Append(": ").Append(f.Type);
                if (f.TargetEntity is { } target) sb.Append(" -> ").Append(target);
                if (f.Required == true) sb.Append(" (required)");
                sb.AppendLine();
            }
        }

        var calculated = e.FieldList.Where(f => f.Calc is not null).ToList();
        if (calculated.Count > 0)
        {
            sb.AppendLine().AppendLine("calculated:");
            foreach (var f in calculated)
                sb.Append("  ").Append(f.Key).Append(": ").AppendLine(Describe(f.Calc!));
        }

        // What points here. The author needs it to write an aggregate — it is the set of entities
        // `over: "mine"` could add up — and working it out otherwise means reading every other entity.
        var incoming = app.EntityList
            .SelectMany(other => other.FieldList
                .Where(f => f.TargetEntity == e.Key && f.TargetApp is null && other.Key != e.Key)
                .Select(f => $"  {other.Key}.{f.Key}"))
            .ToList();
        if (incoming.Count > 0)
        {
            sb.AppendLine().AppendLine("referenced by:");
            foreach (var line in incoming) sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private static string Describe(CordCalc calc) => calc switch
    {
        CordExpr e => e.Expr,
        CordAggregate a =>
            $"{a.Op}({(a.Field is { } f ? $"{a.Of}.{f}" : a.Of)})"
            + (a.Over == CordAggregate.Mine ? " of mine" : $" over {a.Over}")
            + a.During switch
            {
                CordCovering c => $", while covering {c.MyPoint}",
                CordInside i => $", falling inside {i.MyFrom}..{i.MyTo ?? ""}",
                _ => "",
            },
        _ => "?",
    };

    private static string Names(CordApp app) =>
        app.EntityList.Count == 0 ? "no entities" : string.Join(", ", app.EntityList.Select(e => e.Key));

    // ---- screens ---------------------------------------------------------------------------------

    private static string ScreenNames(CordApp app)
    {
        var modelled = app.Screens?.Select(s => s.Key) ?? [];
        var preserved = (app.Raw?["pages"] as JsonArray)?.OfType<JsonObject>()
            .Select(p => Text(p, "key")).OfType<string>() ?? [];
        return string.Join(", ", modelled.Concat(preserved).Where(k => !string.IsNullOrEmpty(k)));
    }

    private static JsonObject? RawPage(CordApp app, string key) =>
        (app.Raw?["pages"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(p => Text(p, "key") == key);

    private static string? RefusalOf(CordApp app) =>
        app.Raw is { } raw && raw["pages"] is JsonArray ? CordImportScreens.Explain(raw) : null;

    private static string Modelled(CordScreen screen)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{screen.Label ?? screen.Key} [{screen.Key}]"
                      + (screen.Subject is { } s ? $", about {s}" : ""));
        if (screen.SectionList.Count > 0) sb.AppendLine().AppendLine("Sections, in order:");
        foreach (var section in screen.SectionList) sb.AppendLine("- " + Section(section));

        foreach (var tab in screen.Tabs ?? [])
        {
            sb.AppendLine().AppendLine($"Tab '{tab.Label ?? tab.Key}' [{tab.Key}]:");
            foreach (var section in tab.Sections ?? []) sb.AppendLine("- " + Section(section));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Section(CordSection section) =>
        section.Kind
        + (section.Of is { } of ? $" of {of}" : "")
        + (section.Label is { } l ? $" — {l}" : "")
        + (section.View is { } v ? $", shown as a {v}" : "")
        + (section.Value is { } m ? $", {m.Op}{(m.Field is { } f ? $" of {f}" : "")}" : "")
        + (section.GroupBy is { } g ? $", split by {g}" : "")
        + (section.Filter is { Count: > 0 } fs ? $", filtered on {fs.Count}" : "");

    /// <summary>A preserved page, read out of the definition it was imported with. Best-effort by
    /// design: this describes shapes Cord has no vocabulary for, so an unrecognised block is NAMED
    /// rather than dropped — a reader has to be able to tell that something is there.</summary>
    private static string Preserved(JsonObject page, JsonArray? views, string? refusal)
    {
        var byKey = (views?.OfType<JsonObject>() ?? [])
            .Where(v => Text(v, "key") is { Length: > 0 })
            .ToDictionary(v => Text(v, "key")!, v => v, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine($"{Text(page, "label") ?? Text(page, "key")} [{Text(page, "key")}]"
                      + (Text(page, "entity") is { } e ? $", about {e}" : ""));
        sb.AppendLine("This screen is preserved from the imported app exactly as it was. You can "
                    + "read it and talk about it; it cannot be edited with a screen operation yet"
                    + (refusal is null ? "." : $", because {refusal}."));

        sb.AppendLine().AppendLine("What is on it, in order:");
        Blocks(page["blocks"] as JsonArray, byKey, sb, "- ");
        return sb.ToString().TrimEnd();
    }

    private static void Blocks(JsonArray? blocks, Dictionary<string, JsonObject> views,
        StringBuilder sb, string indent)
    {
        foreach (var block in (blocks ?? []).OfType<JsonObject>())
        {
            var kind = Text(block, "kind");
            switch (kind)
            {
                case "view" when Text(block, "view") is { } key && views.TryGetValue(key, out var view):
                    sb.AppendLine($"{indent}a {Text(view, "type") ?? "list"} of "
                                  + $"{Text(view, "entity")} — {Text(view, "label")}"
                                  + ((view["filters"] as JsonArray)?.Count is > 0 and var n
                                      ? $" (filtered on {n})" : ""));
                    break;
                case "text":
                    sb.AppendLine($"{indent}a note: {Text(block, "text")}");
                    break;
                case "row" or "columns" or "card":
                    sb.AppendLine($"{indent}{Label(block) ?? kind}:");
                    Blocks(block["blocks"] as JsonArray, views, sb, indent + "  ");
                    foreach (var column in (block["columns"] as JsonArray ?? []).OfType<JsonArray>())
                        Blocks(column, views, sb, indent + "  ");
                    break;
                case "tabs":
                    foreach (var tab in (block["tabs"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        sb.AppendLine($"{indent}tab '{Text(tab, "label") ?? Text(tab, "key")}':");
                        Blocks(tab["blocks"] as JsonArray, views, sb, indent + "  ");
                    }
                    break;
                case "stat" or "chart":
                    sb.AppendLine($"{indent}a {kind}"
                                  + (block["source"] is JsonObject src
                                      ? $" of {Text(src, "entity")}" : "")
                                  + (Label(block) is { } l ? $" — {l}" : ""));
                    break;
                default:
                    // Named, not dropped. "there is something here I cannot describe" is information.
                    sb.AppendLine($"{indent}a {kind ?? "block"}{(Label(block) is { } o ? $" — {o}" : "")}");
                    break;
            }
        }
    }

    private static string? Label(JsonObject block) => Text(block, "label") ?? Text(block, "title");

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
}
