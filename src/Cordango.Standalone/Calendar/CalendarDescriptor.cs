// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Conditions;

namespace Cordango.Standalone.Calendar;

/// <summary>One option of the status field an entry takes its colour and its wording from.</summary>
/// <param name="Value">The stored code.</param>
/// <param name="Label">What a person reads.</param>
/// <param name="Color">The option's colour, where the definition gave one.</param>
/// <param name="Phase">The process phase, which is what makes a pending entry LOOK different from an
/// approved one rather than merely be labelled differently.</param>
public sealed record CalendarOption(string Value, string Label, string? Color, string? Phase);

/// <summary>
/// One entity that puts its records in somebody's calendar, resolved.
///
/// <para><b>Every field here was worked out by the compiler, not by this.</b> <c>CalendarResolver</c>
/// turns an entity's <c>calendar</c> flag into a binding at build time — which date starts an entry,
/// which person owns it, what it is called — and refuses the build when it cannot. The generator
/// copies that answer into source. So a generated application never derives any of it, which is the
/// same bargain <see cref="Forms.FormsDescriptor"/> makes and for the same reason: a fact worked out
/// twice is a fact that drifts, and here the drift would put a record in the wrong person's
/// calendar.</para>
/// </summary>
/// <param name="Entity">The entity key as the definition spells it.</param>
/// <param name="Start">The date or datetime field an entry begins on.</param>
/// <param name="End">The field it ends on, or null for a point in time.</param>
/// <param name="Who">The person reference that decides whose calendar this is. It lives on this
/// entity, or — when <paramref name="Via"/> is set — on the parent.</param>
/// <param name="Via">The reference on THIS entity pointing at the parent that carries
/// <paramref name="Who"/>, or null when the person is on the entity itself. This is the hop that
/// puts a milestone in the calendar of whoever leads its project with nothing on the milestone
/// naming a person.</param>
/// <param name="Parent">The entity <paramref name="Who"/> lives on when <paramref name="Via"/> is
/// set. Null otherwise.</param>
/// <param name="Title">A field key, or a <c>{{field}}</c> template over this entity's fields.</param>
/// <param name="AllDay">Whole days rather than a time of day.</param>
/// <param name="StatusField">The field the colour and state come from, or null.</param>
/// <param name="Options">That field's options, in definition order.</param>
/// <param name="HideWhen">Records that stay OUT of the calendar — a declined request, a cancelled
/// interview. Evaluated by the one shared evaluator, so it means exactly what a command guard
/// means.</param>
/// <param name="StartWritable">Whether the start field is one a role could ever write. A computed or
/// read-only date is a date the calendar shows and cannot move.</param>
/// <param name="EndWritable">The same, for the end.</param>
public sealed record CalendarSource(
    string Entity,
    string Start,
    string? End,
    string Who,
    string? Via,
    string? Parent,
    string Title,
    bool AllDay,
    string? StatusField,
    IReadOnlyList<CalendarOption> Options,
    Condition? HideWhen,
    bool StartWritable,
    bool EndWritable);

/// <summary>
/// Every entity of this application that appears in people's calendars.
///
/// <para><b>What this is NOT.</b> Cordango Platform's calendar spans every app in a workspace: one
/// person, one feed, twelve applications. A generated application is one application, and nothing
/// here pretends otherwise. What it does do is the part that IS true of one app — the flag says
/// "these records belong in the responsible person's calendar", and one application can honour that
/// completely for its own records. Saying so is the difference between a feature that is smaller
/// than the platform's and a flag that compiles to silence.</para>
///
/// <para>Absent entirely when no entity opts in, and the endpoint then answers 404 like any other
/// address that is not there.</para>
/// </summary>
public sealed record CalendarDescriptor(IReadOnlyList<CalendarSource> Sources);
