// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cord;

/// <summary>
/// A screen: <b>a named question somebody needs answered</b>, and the sections that answer it.
///
/// <para><b>This replaces an entity-keyed screen, and the reason is worth recording.</b> The first
/// version was <c>{entity, label, view}</c> — one screen, one entity — which made a whole class of real
/// application unauthorable. "Investor Overview" in the budget planner spans four entities; a "My Day"
/// spans however many kinds of work land on a person's desk. The runtime renders those today. Cord
/// simply had no way to SAY them, which is the same wall the missing app identity was: invisible until
/// somebody hits it, and indistinguishable from the model being bad at its job.</para>
///
/// <para><b>Why the corpus did not catch it.</b> 53 of 60 corpus pages reference exactly one entity, so
/// measuring the corpus said "screens are per-entity" quite clearly. Then: every app with a
/// multi-entity page (room_booking, ventures, budget_planner, timesheets) is one a PERSON designed, and
/// all eleven produced by the generator have none. The corpus was describing the generator's ceiling,
/// not what applications need — and designing the authoring surface from it would have cemented the
/// limitation Cord exists to remove.</para>
///
/// <para><b>Layout is not authored.</b> Sections say what belongs on the screen; whether four metrics
/// become a row of cards is the lowerer's decision. In the hand-designed apps the top-level block kinds
/// are overwhelmingly containers — card 24, stack 18, row 18, columns 11 — carrying a much smaller set
/// of content kinds. Making an author choose between <c>row</c> and <c>stack</c> is exactly the
/// mechanical detail this layer exists to absorb.</para>
/// </summary>
/// <param name="Subject">The entity this screen is ABOUT, when there is one. Context, not content: a
/// screen with a subject can still show sections over other entities. Omit for a screen that belongs to
/// no single record type.</param>
/// <param name="Tabs">Named tabs, when the screen's perspectives share one context.
///
/// <para><b>Sections and tabs COEXIST — this is not an either/or.</b> The Budget Planner's Hiring
/// Plan is the shape that settled it: a headline row of three metrics that stays put, and beneath it
/// four tabs (Plan, Timeline, By Team, Cost) that swap. Modelling tabs as an alternative to sections
/// would have made that screen unauthorable, which is the same wall the entity-keyed screen was.</para>
///
/// <para>Tabs are not renderer mechanics. Whether four metrics become a row of cards is derivable;
/// whether four perspectives are one destination or four is an INFORMATION-ARCHITECTURE claim about
/// shared context that nothing in the sections implies — so it is authored, and it is a decision the
/// co-creation flow asks about with pictures.</para></param>
public sealed record CordScreen(
    string? Key = null,
    string? Label = null,
    string? Subject = null,
    IReadOnlyList<CordSection>? Sections = null,
    IReadOnlyList<CordTab>? Tabs = null)
{
    public IReadOnlyList<CordSection> SectionList => Sections ?? [];

    public IReadOnlyList<CordTab> TabList => Tabs ?? [];

    /// <summary>Every section on this screen, shared and inside tabs, at the top level only — the
    /// two places an author puts content. Splits' children are reached separately where nesting
    /// matters.</summary>
    public IEnumerable<CordSection> AllSections =>
        SectionList.Concat(TabList.SelectMany(t => t.SectionList));

    /// <summary>Every entity this screen names, subject and sections alike. What
    /// <see cref="CordCheck"/> resolves — a screen is only as valid as the entities it points at.</summary>
    public IEnumerable<string> EntityKeys =>
        (Subject is null ? [] : new[] { Subject })
        // Through a split as well as over it: an entity named only inside one is still an entity this
        // screen points at, and missing it would let a split smuggle an unknown key past the check.
        .Concat(AllSections.Concat(AllSections.SelectMany(s => s.SectionList))
            .Select(s => s.Of).Where(x => x is not null).Select(x => x!));
}

/// <summary>
/// One named tab of a screen: a child aggregate, the way a field is a child of its entity.
///
/// <para>That is why it has its own operations. Revising one tab through a whole-screen upsert would
/// mean restating the other three, and an author who has to restate accepted work to change one thing
/// is being asked to risk it — <see cref="UpsertScreenTab"/> exists so the blast radius of "change
/// the cost tab" is the cost tab.</para>
/// </summary>
/// <param name="Key">Stable identity, unique within its screen. Never derived from the label or the
/// position: a tab that is renamed or moved is the same tab, and a deep link into it must survive
/// both.</param>
public sealed record CordTab(
    string Key,
    string? Label = null,
    IReadOnlyList<CordSection>? Sections = null)
{
    public IReadOnlyList<CordSection> SectionList => Sections ?? [];
}

/// <summary>
/// One part of a screen's answer.
/// </summary>
/// <param name="Kind"><c>list</c> | <c>metric</c> | <c>chart</c> | <c>text</c>. Derived from what the
/// hand-designed apps actually use — stat 61, chart 20, view 11, text 13 — rather than from the block
/// catalog, which offers 41 kinds and would put the whole union back in the prefix.</param>
/// <param name="Of">The entity this section draws on. <b>Per section, not per screen</b> — that is the
/// entire point of the shape.</param>
/// <param name="View">For <c>list</c>: a key of <see cref="CordVocabulary.Views"/>.</param>
/// <param name="DateField">For <c>view: "calendar"</c>: WHICH date puts a record on the calendar.
///
/// <para><b>Authored, never inferred, and a real defect is the reason.</b> The renderer falls back to
/// an entity's FIRST date field when a calendar names none, which validates and is frequently wrong:
/// the smoke run authored a calendar called "Decisions due" over an entity whose four dates begin with
/// <c>submitted_on</c>, so it would have shown when each claim was SENT under a heading promising when
/// a decision was DUE. A fallback is a reasonable last resort for a renderer; it is not authoring
/// semantics. If the author did not say which date the calendar is about, nobody knows.</para></param>
/// <param name="Filter">Which records this section is about. <c>{{actor.id}}</c> is the signed-in
/// person, which is how "my tickets" differs from "all tickets" — measured as the single most common
/// distinction between two views of one entity in the corpus.</param>
/// <param name="Value">For <c>metric</c> and <c>chart</c>: what is counted or summed.</param>
/// <param name="Sections">For <c>split</c>: exactly the two things that sit side by side.
///
/// <para><b>The one arrangement an author may state, and the corpus is why it is the only one.</b> All
/// 21 <c>columns</c> blocks across the 15 reference apps hold exactly TWO columns — not one of them
/// holds three — and 19 of the 21 are equal width. So a two-up split with an optional ratio covers every
/// side-by-side layout a person has actually designed here, and a general grid would be vocabulary
/// bought on speculation.</para>
///
/// <para>This is the boundary the rest of this file defends from the other side: putting two charts
/// beside each other is an AUTHOR'S statement that they are read together, which nothing can derive from
/// a list of sections. Whether four metrics become a row of cards is RENDERER MECHANICS, derivable, and
/// stays out of the vocabulary. Splits do not nest — a split inside a split is a grid, which is the
/// thing this deliberately is not.</para></param>
/// <param name="Ratio">For <c>split</c>: relative widths, two positive numbers. Defaults to equal, which
/// is what 19 of the corpus's 21 two-column blocks use; the two that differ are <c>[2,1]</c> and
/// <c>[3,2]</c>, both a chart beside something supporting it.</param>
/// <param name="Visual">For <c>chart</c>: <c>auto</c> | <c>bar</c> | <c>line</c> | <c>area</c> |
/// <c>donut</c>. <c>auto</c> is the default and keeps the existing inference — a grouped measure is a
/// donut, an ungrouped one a bar. The named values exist because that inference cannot know a time
/// series is a time series: a monthly runway is a LINE and no property of the measure says so.</param>
/// <param name="Key">Stable identity for a section that emits a view (<c>list</c>, <c>metric</c>,
/// <c>chart</c>), unique within its screen including inside tabs.
///
/// <para>The lowered view key is derived from it, so identity never comes from a label or an ordinal:
/// renaming a section or moving it must not renumber anything, because deep links, personal views and
/// per-user table settings are keyed on the lowered key. <c>text</c> and <c>split</c> emit no view and
/// need none.</para></param>
public sealed record CordSection(
    string? Key = null,
    string? Kind = null,
    string? Of = null,
    string? Label = null,
    string? View = null,
    IReadOnlyList<CordWhen>? Filter = null,
    IReadOnlyList<CordSort>? Sort = null,
    IReadOnlyList<string>? Columns = null,
    string? DateField = null,
    string? GroupBy = null,
    CordMeasure? Value = null,
    string? Text = null,
    IReadOnlyList<CordSection>? Sections = null,
    IReadOnlyList<double>? Ratio = null,
    string? Visual = null)
{
    public IReadOnlyList<CordSection> SectionList => Sections ?? [];
}

/// <summary>The section kinds, named once so the schema, the lowerer and the tests cannot disagree —
/// the same discipline <see cref="CordVocabulary"/> exists for, and for the same reason: two hand-written
/// lists of one vocabulary is how `board` came to be offered and unlowerable.</summary>
public static class CordSectionKinds
{
    public const string List = "list";
    public const string Metric = "metric";
    public const string Chart = "chart";
    public const string Text = "text";

    /// <summary>Two content sections side by side. A LAYOUT kind, and the only one — see
    /// <see cref="CordSection.Sections"/> for why the corpus justifies exactly this and nothing more.</summary>
    public const string Split = "split";

    /// <summary>What a section can BE. Valid inside a split as well as at the top of a screen.</summary>
    public static readonly IReadOnlyList<string> Content = [List, Metric, Chart, Text];

    /// <summary>Everything a TOP-LEVEL section can be. Content plus the one arrangement.</summary>
    public static readonly IReadOnlyList<string> All = [.. Content, Split];
}

/// <param name="Op"><c>count</c> | <c>sum</c> | <c>avg</c> | <c>min</c> | <c>max</c>.</param>
/// <param name="GroupBy">For a chart: the field the bars or slices are split by.</param>
public sealed record CordMeasure(string Op, string? Field = null, string? GroupBy = null);

/// <param name="Direction"><c>asc</c> or <c>desc</c>.</param>
public sealed record CordSort(string Field, string? Direction = null);
