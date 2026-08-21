// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition;

/// <summary>
/// The AUTHORING vocabulary for blocks: namespaced component families the AI writes
/// (<c>layout.row</c>, <c>data.repeat</c>, <c>domain.hub</c>) mapped onto the CANONICAL flat
/// block kinds that the stored schema, the gate, the compiler and the renderer speak
/// (<c>row</c>, <c>repeat</c>, <c>hub</c>).
///
/// <para><b>Why.</b> The canonical <c>block.kind</c> is one flat 27-value enum on a polymorphic
/// node with ~50 properties — fine as runtime bytecode, poor as an authoring language: the model
/// picks from an undifferentiated list and valid-but-wrong is one property away. Grouping the
/// vocabulary into five families gives it a real mental model (<c>layout.*</c> = 8 containers, not
/// 27 options) and self-documenting ids.</para>
///
/// <para><b>Where it applies.</b> Authoring names exist ONLY at the model boundary:
/// <see cref="Schemas.AuthoringSchema"/> swaps them into the tool input_schema the model targets,
/// and <see cref="Normalizer.LowerAuthoringKinds"/> lowers them back to canonical BEFORE the gate.
/// Nothing downstream — schema file, gate, compiler, renderer, stored definitions — ever sees a
/// dotted kind, so existing apps keep validating unchanged. The same map covers
/// <c>$defs.formBlock</c> (its <c>section</c>/<c>fields</c> are <c>layout.section</c>/
/// <c>display.fields</c>), so the model never sees a mixed dialect.</para>
///
/// <para><b>Deprecation-by-omission.</b> <c>widgets</c> (the untyped KPI/chart array) deliberately
/// has NO authoring name: it stays legal canonically so existing definitions validate, but the
/// model cannot emit it because it is absent from the authoring enum. This is the
/// "deprecate at authoring only" recommendation in COMPOSITION_REFRAME_FINAL.md §8.</para>
/// </summary>
public static class BlockKinds
{
    /// <summary>One authoring name. <see cref="PresetProperty"/>/<see cref="PresetValue"/> let a
    /// dotted name carry a discriminator that is a separate property canonically — lowering
    /// <c>control.segmented</c> yields <c>{kind:"control", control:"segmented"}</c>, so the model
    /// never has to remember the extra field.</summary>
    public sealed record AuthoringKind(
        string Name,
        string Family,
        string Canonical,
        string? PresetProperty = null,
        string? PresetValue = null);

    /// <summary>The five families, in the order they are presented to the model.</summary>
    public static readonly IReadOnlyList<string> Families = ["layout", "data", "display", "domain", "control"];

    /// <summary>
    /// Canonical kinds that are intentionally NOT authorable: legal in a stored document, absent from
    /// the model-facing enum.
    ///
    /// <para>Two different reasons live here, and the difference matters when deciding whether to add
    /// something:</para>
    /// <list type="bullet">
    /// <item><c>widgets</c> — <b>deprecated</b>. Superseded at authoring; still rendered so older
    /// stored definitions keep working.</item>
    /// <item><c>externalEmbed</c> — <b>withheld on purpose</b>. It loads a third party's code into
    /// the browser of whoever opens the record, which is a decision a person makes with a name and a
    /// date against it (a platform admin approves the provider; the viewer then consents in their own
    /// browser). A generator that could emit one would be making that decision on their behalf, in an
    /// app nobody has read yet. It is placed by hand, in a core definition, or not at all.</item>
    /// <item><c>relatedApps</c> — <b>withheld because it is about the workspace, not the app</b>. It
    /// shows what OTHER apps hold about a record, which is a platform surface that happens to render
    /// inside one app's detail; a generated app authoring it would be claiming a view over apps it
    /// knows nothing about. It belongs on core entities other apps reference, placed by hand.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> NotAuthorable =
        new HashSet<string>(StringComparer.Ordinal) { "widgets", "externalEmbed", "relatedApps" };

    /// <summary>The full authoring vocabulary.</summary>
    public static readonly IReadOnlyList<AuthoringKind> All =
    [
        // ---- layout: containers that arrange other blocks ----
        new("layout.stack",    "layout",  "stack"),
        new("layout.row",      "layout",  "row"),
        new("layout.grid",     "layout",  "grid"),
        new("layout.card",     "layout",  "card"),
        new("layout.tabs",     "layout",  "tabs"),
        new("layout.section",  "layout",  "section"),
        new("layout.columns",  "layout",  "columns"),
        new("layout.split",    "layout",  "split"),

        // ---- data: blocks that read records/axes and render them ----
        new("data.view",       "data",    "view"),
        new("data.table",      "data",    "table"),
        new("data.repeat",     "data",    "repeat"),
        new("data.cell",       "data",    "cell"),
        new("data.stat",       "data",    "stat"),
        new("data.tiles",      "data",    "tiles"),
        new("data.chart",      "data",    "chart"),
        new("data.calendar",   "data",    "calendar"),
        new("data.timeline",   "data",    "timeline"),
        new("data.orgchart",   "data",    "orgchart"),
        new("data.board",      "data",    "board"),
        new("data.gantt",      "data",    "gantt"),

        // ---- display: leaves that print one value of the bound row/record ----
        new("display.text",     "display", "text"),
        new("display.field",    "display", "field"),
        new("display.fields",   "display", "fields"),
        new("display.chip",     "display", "chip"),
        new("display.avatar",   "display", "avatar"),
        new("display.progress", "display", "progress"),

        // ---- domain: the app's own machinery (identity, state, children, verbs) ----
        new("domain.hub",       "domain",  "hub"),
        new("domain.process",   "domain",  "process"),
        new("domain.child",     "domain",  "child"),
        new("domain.action",    "domain",  "action"),
        new("domain.create",    "domain",  "create"),
        new("domain.settings",  "domain",  "settings"),
        new("domain.form",      "domain",  "form"),
        new("domain.history",   "domain",  "history"),
        new("domain.answers",   "domain",  "answers"),
        new("domain.intake",    "domain",  "intake"),

        // ---- control: writes page state, runs no command and touches no data ----
        new("control.segmented", "control", "control", "control", "segmented"),
        new("control.stepper",   "control", "control", "control", "stepper"),
        new("control.filterbar", "control", "filterbar"),
    ];

    /// <summary>Authoring name -> entry.</summary>
    public static readonly IReadOnlyDictionary<string, AuthoringKind> ByName =
        All.ToDictionary(k => k.Name, StringComparer.Ordinal);

    /// <summary>Canonical kind -> the authoring names that lower to it.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByCanonical =
        All.GroupBy(k => k.Canonical, StringComparer.Ordinal)
           .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(k => k.Name).ToList(),
                         StringComparer.Ordinal);

    /// <summary>Every authoring name, grouped family-by-family (the order the model sees).</summary>
    public static IReadOnlyList<string> Names =>
        Families.SelectMany(NamesFor).ToList();

    /// <summary>The authoring names in one family, in declaration order.</summary>
    public static IReadOnlyList<string> NamesFor(string family) =>
        All.Where(k => k.Family == family).Select(k => k.Name).ToList();

    public static AuthoringKind? Find(string name) =>
        ByName.TryGetValue(name, out var k) ? k : null;

    public static bool IsAuthoringName(string? name) =>
        name is not null && ByName.ContainsKey(name);

    /// <summary>One-line-per-family guide inlined into the model-facing enum's description.</summary>
    public static string FamilyGuide()
    {
        string[] blurbs =
        [
            "layout.* = containers that arrange other blocks",
            "data.* = blocks that read records (or a date/option/platform axis) and render them",
            "display.* = leaves that print one value of the bound row/record",
            "domain.* = the app's own machinery: identity header, state stepper, child records, command buttons, settings",
            "control.* = writes page state only; runs no command and touches no data",
        ];
        return string.Join("\n", Families.Select((f, i) =>
            $"{blurbs[i]} — {string.Join(", ", NamesFor(f))}"));
    }
}
