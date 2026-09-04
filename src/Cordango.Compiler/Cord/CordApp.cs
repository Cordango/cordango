// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The semantic application model — what an author means, rather than what the runtime consumes.
///
/// <para>An App Definition is the compiled contract: 81 <c>$defs</c>, 41 block kinds, and a shape in
/// which valid-but-wrong is one property away. CordApp is the layer in front of it, small enough that
/// the constructs an author reasons about (an entity, a calculation, a screen pattern) are the
/// constructs they write. <see cref="CordLower"/> turns one into the other.</para>
///
/// <para><b><c>Raw</c> is scaffolding, not a feature.</b> Every node can carry an opaque App Definition
/// fragment for exactly one reason: <see cref="CordImport"/> has to be TOTAL from the first commit, so
/// Cord can open an app it did not create — one hand-edited in the WYSIWYG editor, one refined by an
/// older model, one that does not pass today's gate at all. Without it, the authoring layer could only
/// ever touch applications it had authored itself.</para>
///
/// <para><b>It is expected to shrink, and it is measured.</b> Every remaining fragment is a line in
/// <c>exploration/cord/raw-allowlist.json</c> with a reason, and the tests assert that set EXACTLY —
/// not as a subset — so an escape hatch cannot appear without a reviewed diff, and modelling cannot be
/// quietly removed without breaking the build. Today's overlay is dominated by whole sections later
/// slices own (behaviour, screens); it collapses as those land.</para>
///
/// <para><b>It is never a MODEL authoring operation.</b> There is no <c>set_raw_json</c> in the tool
/// vocabulary; the mutation ops carry no raw variant at all. Reading anything is the point; writing
/// anything is how the semantic model would get bypassed one convenient exception at a time. A human
/// may edit an already-imported remainder through the advanced editor; that path is restricted to
/// fragments already present, validates the complete definition, and records internal journal
/// recovery vocabulary rather than teaching the model a raw escape hatch.</para>
///
/// <para>Mechanically it is an OVERLAY, not a replacement: <see cref="CordLower"/> emits what the node
/// models and then deep-merges <c>Raw</c> over the top, raw winning. That is what makes modelling
/// PARTIAL — a field whose <c>key</c>, <c>label</c> and <c>type</c> are understood but whose
/// <c>treeAggregate</c> is not keeps most of its coverage instead of dropping to zero — so coverage
/// climbs continuously instead of in cliffs.</para>
/// </summary>
/// <param name="SchemaVersion">The contract version. Carried, never authored — it is the platform's
/// fact, stamped by <c>AppSchemaVersion</c>, and no operation will offer it.</param>
/// <param name="Raw">Root properties this model does not represent — today the UI and behaviour
/// sections, which arrive in later slices. Never contains a key the model DOES represent;
/// <see cref="CordCoverage"/> asserts that, because a node imported wrongly and then overlaid with raw
/// would round-trip perfectly while scoring as covered.</param>
public sealed record CordApp(
    string? Key = null,
    string? Name = null,
    string? Version = null,
    string? Description = null,
    string? SchemaVersion = null,
    IReadOnlyList<CordEntity>? Entities = null,
    IReadOnlyList<CordScreen>? Screens = null,
    IReadOnlyList<CordProcess>? Processes = null,
    IReadOnlyList<CordAction>? Actions = null,
    IReadOnlyList<CordSchedule>? Schedules = null,
    IReadOnlyList<CordRole>? Roles = null,
    JsonObject? Raw = null,
    CordPurpose? Purpose = null,
    IReadOnlyList<CordUse>? Uses = null)
{
    public IReadOnlyList<CordUse> UseList => Uses ?? [];
    public IReadOnlyList<CordEntity> EntityList => Entities ?? [];
    public IReadOnlyList<CordProcess> ProcessList => Processes ?? [];
    public IReadOnlyList<CordAction> ActionList => Actions ?? [];
    public IReadOnlyList<CordSchedule> ScheduleList => Schedules ?? [];
    public IReadOnlyList<CordRole> RoleList => Roles ?? [];

    /// <summary>App Definition root properties this model represents. Named once so the importer and
    /// the lowerer cannot disagree about what is modelled — which is what would make a pointer both
    /// semantic and raw.
    ///
    /// <para><c>commands</c> is here but has no field of its own: it is produced by lowering
    /// <see cref="CordProcess"/> transitions and <see cref="CordAction"/>s together. That is the whole
    /// point of the behaviour slice — the author never files a command beside the transition that uses
    /// it.</para></summary>
    public static readonly string[] Modelled =
    [
        "key", "name", "version", "description", "schemaVersion", "entities",
        "pages", "views", "processes", "commands", "workflows", "roles",
        "purpose", "uses",
    ];
}

/// <summary>
/// Why the application exists — the one thing about an app that no compiler can work out.
///
/// <para>Everything else the App Contract states is derived from what is authored. This is not:
/// nothing in a list of entities says whether they are there to run a sales pipeline or to record
/// its aftermath. Written once, when the app is created, by whoever was told what it is for.</para>
/// </summary>
/// <param name="Duties">What this app OWNS, which is a claim other apps may build on — not what it
/// merely displays.</param>
public sealed record CordPurpose(
    string? Summary = null,
    IReadOnlyList<string>? Duties = null)
{
    public IReadOnlyList<string> DutyList => Duties ?? [];
}

/// <summary>
/// One other application this one deliberately builds on.
///
/// <para>Distinct from a reference field pointing at it: the field is a link, this is the statement
/// that the link is intended. An author declares Organizations once, and every field that points
/// there is an instance of that decision rather than a decision of its own.</para>
/// </summary>
/// <param name="App">A core app's systemKey, or another app's key. Never a handle.</param>
/// <param name="Entities">Which of its entities are used. Empty means all of them.</param>
public sealed record CordUse(
    string? App = null,
    IReadOnlyList<string>? Entities = null,
    string? Why = null)
{
    public IReadOnlyList<string> EntityList => Entities ?? [];
}

/// <summary>
/// One thing the application keeps records of.
/// </summary>
/// <param name="Kind">Surface intent: <c>collection</c> (browsable), <c>config</c> (a lookup list
/// filed under Configuration), <c>settings</c> (a singleton rendered as a form).</param>
/// <param name="Owner">Set when this entity only exists inside a parent record — line items,
/// comments, answers. Subordinate entities get no navigation of their own.</param>
/// <param name="Series">Set when rows form an ORDERED SERIES. The precondition for <c>prev()</c>.</param>
/// <param name="Unique">Field combinations no two records may share; each becomes a real database
/// constraint.</param>
/// <param name="Calendar">Set when these records belong in the calendar of whoever is responsible
/// for them. A bare flag on the wire and nothing more: which date and which person it means are
/// worked out by the compiler from what the entity already declares, so the semantic surface never
/// asks an author to name them and never has to be kept in step when the derivation improves.</param>
/// <param name="Raw">Today: <c>detail</c>, <c>peek</c> and <c>form</c> — the authored block trees.
/// Those are screens, they are the UI slice's work, and modelling them here as anonymous JSON would be
/// exactly the "second UI model" that was ruled out.</param>
public sealed record CordEntity(
    string? Key = null,
    string? Label = null,
    string? LabelPlural = null,
    string? Description = null,
    string? Icon = null,
    string? DisplayField = null,
    string? ImageField = null,
    string? Role = null,
    string? Kind = null,
    CordOwnership? Owner = null,
    CordSeries? Series = null,
    IReadOnlyList<CordField>? Fields = null,
    IReadOnlyList<IReadOnlyList<string>>? Unique = null,
    bool? Calendar = null,
    JsonObject? Raw = null)
{
    public IReadOnlyList<CordField> FieldList => Fields ?? [];

    public static readonly string[] Modelled =
    [
        "key", "label", "labelPlural", "description", "icon", "displayField", "imageField",
        "role", "kind", "ownedBy", "series", "fields", "unique", "calendar",
    ];
}

/// <param name="Parent">Parent entity key.</param>
/// <param name="Via">The reference field on the CHILD pointing at the parent. Normally null: it is
/// derived by <see cref="CordRefIndex.SoleReferenceTo"/>, which is unambiguous for all 24 owned
/// entities in the corpus. Set only where inference would not reproduce what a document already said,
/// which keeps the round-trip exact without claiming an inference that did not happen.</param>
/// <param name="As">How the child renders inside the parent's detail. Omit to let the runtime choose.</param>
public sealed record CordOwnership(string Parent, string? Via = null, string? As = null);

/// <param name="Partition">A reference field; rows sharing its value form one series. <c>prev()</c>
/// never reaches across partitions.</param>
/// <param name="Order">A numeric or date field giving the order within the partition.</param>
public sealed record CordSeries(string Partition, string Order);

/// <summary>
/// One piece of information a record holds.
/// </summary>
/// <param name="Calc">Set when the value is derived rather than typed. A field is either something
/// somebody enters or something the platform works out — never both.</param>
/// <param name="TargetApp"><c>platform</c> for the canonical person/department/group, or another app's
/// key. Absent for a local reference.</param>
/// <param name="Raw">What this slice does not model: <c>treeAggregate</c>, <c>mapsTo</c>,
/// <c>optionsFilter</c>, <c>initial</c>. Each is a genuine construct and each is listed in the raw
/// allowlist by name, so none of them can be forgotten quietly.</param>
public sealed record CordField(
    string? Key = null,
    string? Label = null,
    string? Type = null,
    bool? Required = null,
    bool? Unique = null,
    bool? Indexed = null,
    string? Help = null,
    string? Group = null,
    JsonNode? Default = null,
    int? Precision = null,
    int? Scale = null,
    string? Unit = null,
    string? Prefix = null,
    string? Input = null,
    string? Currency = null,
    string? Role = null,
    string? TargetEntity = null,
    string? TargetApp = null,
    string? OnDelete = null,
    IReadOnlyList<CordOption>? Options = null,
    CordCalc? Calc = null,
    JsonObject? Raw = null)
{
    public static readonly string[] Modelled =
    [
        "key", "label", "type", "required", "unique", "indexed", "help", "group", "default",
        "precision", "scale", "unit", "prefix", "input", "currency", "role",
        "targetEntity", "targetApp", "onDelete", "options", "computed",
    ];
}

/// <param name="Phase">Lifecycle meaning for a status select with no governing process.</param>
public sealed record CordOption(
    string Value,
    string Label,
    string? Color = null,
    string? Phase = null,
    JsonObject? Raw = null);
