// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cord;

/// <summary>
/// One Cord vocabulary: the words the model is offered, and what each lowers to.
///
/// <para><b>The single most expensive class of defect this project has had, solved once instead of five
/// times.</b> A word Cord offers that Cord cannot lower produces a document the gate rejects for a value
/// the model picked out of the vocabulary Cord itself handed it. From every log and every dashboard that
/// reads as the model being bad at its job. It is not — and it cost a real generation run before anyone
/// worked out what had happened.</para>
///
/// <para>The fix is structural rather than careful: <b>the schema's enum and the lowerer's translation
/// are the same data</b>, so offering a word Cord cannot lower is not something a person can get wrong.
/// <c>CordSchemaSizeTests</c> closes the loop by checking every lowered value against the enum in the
/// real App Definition schema, which is the assertion that would have caught all five.</para>
///
/// <para><b>Cord keeps its own words where they are better ones.</b> <c>board</c> says what the thing IS
/// to the person asking for it; <c>kanban</c> is the renderer's name for how it is drawn. <c>rowMenu</c>
/// says where a button appears; <c>tableRow</c> names a component. Translating is the lowerer's job,
/// which is the whole premise of a semantic layer. Where the platform's word is already the plain one —
/// a state's <c>phase</c> — the map is an identity and says so.</para>
/// </summary>
/// <param name="Name">What this vocabulary is, for test output and error messages.</param>
public sealed record CordWordMap(string Name, IReadOnlyList<(string Word, string Lowered)> Pairs)
{
    /// <summary>
    /// Platform values Cord deliberately does NOT offer, each with the reason.
    ///
    /// <para><b>Why a narrower vocabulary has to say so out loud.</b> Three live agent reports in one
    /// day all had the same shape: the decision was right and the reason was unreachable. An author
    /// who cannot find <c>contains</c> concludes the tool is broken and works around it — one modelled
    /// seven boolean columns rather than one multiselect, and said so. The workaround is not the
    /// failure; the silence is. A withheld word with a reason attached is a closed question. A missing
    /// word is an open one that every author reopens.</para>
    ///
    /// <para>Cord being narrower than the platform is often CORRECT — an external effect that can send
    /// mail is not something a model-facing surface should hand out casually. What is never correct is
    /// leaving an author to guess which kind of absence they have hit.</para>
    /// </summary>
    public IReadOnlyList<(string Word, string Because)> Withheld { get; init; } = [];

    /// <summary>The words the model may use, in a stable order so the tool schema is byte-stable across
    /// runs and the prompt cache is not invalidated by dictionary ordering.</summary>
    public IReadOnlyList<string> Words { get; } = [.. Pairs.Select(p => p.Word)];

    /// <summary>What those words lower to — the set a test compares against the App Definition's own
    /// enum.</summary>
    public IReadOnlyList<string> LoweredValues { get; } = [.. Pairs.Select(p => p.Lowered).Distinct()];

    private readonly Dictionary<string, string> _lowered =
        Pairs.ToDictionary(p => p.Word, p => p.Lowered, StringComparer.Ordinal);

    /// <summary>The App Definition value for a Cord word.
    ///
    /// <para>An UNKNOWN word lowers to ITSELF rather than to a default, deliberately: the schema
    /// constrains the value, so anything else arriving here means a validated document disagreed with
    /// this map. Substituting something plausible would hide that. Let the gate reject it and name the
    /// value.</para></summary>
    public string? Lower(string? word) =>
        word is null ? null : _lowered.TryGetValue(word, out var value) ? value : word;

    /// <summary>Lower, or a stated fallback when the author said nothing.</summary>
    public string LowerOr(string? word, string fallback) => Lower(word) ?? fallback;

    private readonly Dictionary<string, string> _raised = Pairs
        .GroupBy(p => p.Lowered, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Word, StringComparer.Ordinal);

    /// <summary>The Cord word for an App Definition value — <see cref="Lower"/> run backwards, for
    /// writing a document out of a model that was IMPORTED rather than authored.
    ///
    /// <para>Needed because the importer stores what the document said. An imported command carries
    /// <c>default</c> and <c>tableRow</c>, which are the platform's words; writing them into an
    /// operation unchanged would produce a document the ops schema rejects for a value Cord itself
    /// produced. That is the same failure this class exists to prevent, arriving from the other
    /// direction.</para>
    ///
    /// <para>Returns null when no word maps to it — <c>listHeader</c> is a real example, a placement
    /// the platform has and Cord deliberately does not. Null means UNWRITABLE, and a writer reports it
    /// rather than inventing something close.</para>
    ///
    /// <para>Where two words lower to one value the first pair wins. That is a choice, not a
    /// derivation, so it is stated here rather than left to dictionary order.</para>
    ///
    /// <para><b>A value that is already a word raises to itself</b>, because the model holds both:
    /// <c>schedule.daily</c> when an author wrote it and <c>schedule</c> when the importer read one
    /// back. Insisting on the lowered spelling would make Cord unable to write down an app Cord had
    /// just authored — which is exactly what it did, on the first run of the corpus export.
    /// Unambiguous across every map here, and <c>CordVocabularyTests</c> holds that: no word is any
    /// other pair's lowered value.</para></summary>
    public string? Raise(string? lowered) =>
        lowered is null ? null
        : _raised.TryGetValue(lowered, out var word) ? word
        : _lowered.ContainsKey(lowered) ? lowered
        : null;
}

/// <summary>Every Cord vocabulary whose words differ from the App Definition's, in one place.</summary>
public static class CordVocabulary
{
    /// <summary>How a list of records is shown.</summary>
    public static readonly CordWordMap Views = new("view", [
        ("table", "table"),
        ("board", "kanban"),
        ("calendar", "calendar"),
        ("timeline", "timeline"),
    ])
    {
        Withheld =
        [
            ("detail", "shows ONE record, and this vocabulary answers 'how is a LIST of records "
                + "shown' — a record's own layout is `views/entities/<key>/detail`"),
            ("dashboard", "is a page of mixed content rather than a way of listing one entity's "
                + "records; author it as a screen"),
        ],
    };

    /// <summary>
    /// What sets an automation off.
    ///
    /// <para><c>schedule.daily</c> is the word an author reaches for and <c>schedule</c> is what the
    /// platform calls it; the cron expression that says WHEN travels beside it either way. Before this
    /// map, <b>no Cord application could have a scheduled automation at all</b> — the one word offered
    /// for unattended work lowered to a value the enum does not contain, so chasing a stale approval and
    /// flagging a deal gone quiet were both unauthorable.</para>
    /// </summary>
    public static readonly CordWordMap TriggerEvents = new("automation trigger", [
        ("record.created", "record.created"),
        ("record.updated", "record.updated"),
        ("record.deleted", "record.deleted"),
        ("schedule.daily", "schedule"),
    ])
    {
        Withheld =
        [
            ("field.changed", "not a separate word here — write `record.updated` and name the "
                + "`field`, and it lowers to this"),
        ],
    };

    /// <summary>How prominent a button is. <c>secondary</c> is the ordinary word for what the platform
    /// calls <c>default</c> — same meaning, and neither name is worth teaching twice.</summary>
    public static readonly CordWordMap CommandStyles = new("button style", [
        ("primary", "primary"),
        ("secondary", "default"),
        ("danger", "danger"),
    ]);

    /// <summary>
    /// Where a button appears.
    ///
    /// <para><c>listHeader</c> is GONE rather than mapped. It described a place the platform does not
    /// have, and the two candidates for an approximation — the bulk toolbar and a board card — are
    /// different things that would each have been wrong half the time. The platform's two real
    /// placements are exposed instead, under names that say what the author sees.</para>
    /// </summary>
    public static readonly CordWordMap Placements = new("button placement", [
        ("recordHeader", "recordHeader"),
        ("rowMenu", "tableRow"),
        ("selection", "bulkToolbar"),
        ("boardCard", "kanbanCard"),
    ]);

    /// <summary>
    /// A state's coarse grouping for reporting.
    ///
    /// <para>An identity map, and it exists anyway — because the defect here was not a mismatch but a
    /// FREE STRING. Cord typed <c>phase</c> as any text with the description "coarse grouping for
    /// reporting", so the author could not see that a closed set existed and had no way to discover it
    /// except by being rejected. That is the repo's own doctrine about closed sets, applied to Cord's
    /// own schema.</para>
    /// </summary>
    public static readonly CordWordMap StatePhases = new("state phase", [
        ("not_started", "not_started"),
        ("active", "active"),
        ("done", "done"),
        ("cancelled", "cancelled"),
    ]);

    /// <summary>
    /// What an effect does.
    ///
    /// <para>An identity map over a DELIBERATE SUBSET. The platform also has <c>createForEach</c>, and
    /// Cord does not offer it — so this is not "the effect types", it is "the effect types an author may
    /// write", and the difference is the point of listing them here.</para>
    ///
    /// <para>It was inline in the schema until 2026-08-11, which is how it came to disagree with the
    /// writer: <see cref="CordDocument"/> emitted a corpus app's <c>createForEach</c> straight into an
    /// operation and the ops schema refused a document Cord itself had just written. A closed set with
    /// two copies is a closed set that will drift.</para>
    /// </summary>
    public static readonly CordWordMap EffectTypes = new("effect", [
        ("updateRecord", "updateRecord"),
        ("createRecord", "createRecord"),
        ("notify", "notify"),
    ])
    {
        Withheld =
        [
            ("createForEach", "makes many records from a set at once — powerful enough that it is a "
                + "deliberate omission from the authoring surface rather than an oversight"),
            ("email", "sends mail OUT of the system; external effects are not handed to a model-facing "
                + "vocabulary"),
            ("webhook", "calls a third party; external, same reason as email"),
            ("enrich", "calls a paid external service; external, same reason as email"),
            ("deleteRecord", "removes a record and nothing undoes it. Withheld from the authoring "
                + "vocabulary for the reason the others are not: every effect here writes something "
                + "a person can correct afterwards by editing the row, and this one leaves nothing "
                + "to edit. Author it in the App Definition, where the gate's guard rules apply"),
        ],
    };

    /// <summary>
    /// How a guard compares.
    ///
    /// <para>Also a deliberate subset — the platform's condition language additionally has
    /// <c>contains</c>, <c>in</c>, <c>notIn</c>, <c>between</c> and <c>overlaps</c>. Same reason as
    /// above for naming the eight rather than leaving them inline.</para>
    /// </summary>
    /// <summary>
    /// Comparisons a guard may use.
    ///
    /// <para><b>`contains`, `in` and `notIn` were missing until 2026-08-13, and their absence cost a
    /// real design.</b> An agent building a recurring-task app wanted one "repeats on" multiselect
    /// and a guard testing it; with no set-membership comparison available it modelled seven boolean
    /// columns instead — and said so. The App Definition has allowed all three the whole time, so
    /// this was Cord being NARROWER than the platform, which is the "an unmodelled thing is a wall"
    /// failure: the model does not look limited, it looks stupid, and the vocabulary is the first
    /// place to check.</para>
    ///
    /// <para><c>between</c> and <c>overlaps</c> stay out for now. They are also in the App Definition
    /// enum, but they take a value SHAPE — a pair, a range — that Cord's single
    /// <see cref="CordWhen.Value"/> does not describe, so offering them without modelling the shape
    /// would hand back a word that lowers into a gate rejection. They belong to the relative-date
    /// work in <c>plan-expression-plane.md</c> Wave 1b.</para>
    /// </summary>
    public static readonly CordWordMap ConditionOperators = new("comparison", [
        ("eq", "eq"), ("neq", "neq"),
        ("gt", "gt"), ("gte", "gte"), ("lt", "lt"), ("lte", "lte"),
        ("contains", "contains"), ("in", "in"), ("notIn", "notIn"),
        ("isEmpty", "isEmpty"), ("isNotEmpty", "isNotEmpty"),
    ])
    {
        Withheld =
        [
            ("between", "takes a PAIR of values, which Cord's single `value` cannot describe — "
                + "modelling the shape comes with the relative-date work"),
            ("overlaps", "takes a RANGE, same reason as between"),
        ],
    };

    /// <summary>
    /// How a chart is drawn.
    ///
    /// <para><c>auto</c> is NOT in this map, deliberately — it is not a word that lowers to a chart
    /// type, it is the absence of a choice, and the lowerer keeps inferring (grouped is a donut,
    /// ungrouped a bar) when it is given. Putting it here would mean claiming it lowers to something the
    /// platform's enum contains, which is exactly the lie the enum test exists to catch.</para>
    ///
    /// <para><c>pie</c> is in the platform's enum and not offered: a donut is the same chart and the
    /// corpus never asked for the other one. Naming both would be two words for one decision.</para>
    /// </summary>
    public static readonly CordWordMap ChartVisuals = new("chart visual", [
        ("bar", "bar"),
        ("line", "line"),
        ("area", "area"),
        ("donut", "donut"),
            ("pie", "pie"),
    ]);

    /// <summary>"Work it out." The default, and the only value for <c>visual</c> that is not a
    /// <see cref="ChartVisuals"/> word.</summary>
    public const string AutoVisual = "auto";

    /// <summary>Every map, so a test can walk them all rather than being told about each one.</summary>
    public static readonly IReadOnlyList<CordWordMap> All =
        [Views, TriggerEvents, CommandStyles, Placements, StatePhases, EffectTypes, ConditionOperators,
         ChartVisuals];

    /// <summary>
    /// The trigger event, which is the one that needs more than a word.
    ///
    /// <para>"Run when the status field changes" is <c>record.updated</c> plus a field name in Cord, and
    /// the platform has a distinct event for exactly that. Lowering it as <c>record.updated</c> would be
    /// structurally legal and semantically wrong — the automation would fire on every write instead of
    /// on that field changing — which is the worst kind of mismatch, because nothing rejects it.</para>
    /// </summary>
    /// <summary>Cord's word for "something on this record changed".</summary>
    public const string RecordUpdated = "record.updated";

    /// <summary>What <see cref="RecordUpdated"/> lowers to when a FIELD is named. Not a word an author
    /// writes and not a value in <see cref="TriggerEvents"/> — which is why the inverse cannot live in
    /// a word map and is spelled out by both callers.</summary>
    public const string FieldChanged = "field.changed";

    public static string? TriggerEvent(string? on, string? field) =>
        on == RecordUpdated && !string.IsNullOrEmpty(field) ? FieldChanged : TriggerEvents.Lower(on);
}
