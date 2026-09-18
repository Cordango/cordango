// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNet.Emit;

/// <summary>
/// The entities that put their records in somebody's calendar, written down.
///
/// <para><b>Nothing here is derived.</b> The compiler's <c>CalendarResolver</c> has already turned
/// each entity's <c>calendar</c> flag into a binding — which date starts an entry, whose calendar it
/// lands in, what it is called, which records stay out — and refuses the build where it cannot. By
/// the time the manifest reaches this, every answer is in it. This copies them into source so the
/// runtime never has to work any of it out, and so a generated application cannot disagree with the
/// definition it came from.</para>
///
/// <para><b>What this target does and does not claim.</b> Cordango Platform's calendar spans every
/// app in a workspace. A generated application is one application, and the cross-app part is
/// genuinely not there. The part that IS true of one app — "these records belong in the responsible
/// person's calendar" — is emitted in full, because one application can honour that completely for
/// its own records, and a flag that compiled to silence is how seven earlier gaps went unnoticed.
/// CORD2309 now says only the smaller, true thing.</para>
///
/// <para>Emits nothing when no entity opts in, and the endpoint is then not there at all rather than
/// there and empty.</para>
/// </summary>
public static class CalendarEmitter
{
    public static GeneratedFile? Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var sources = app.Calendars;
        if (sources.Count == 0) return null;

        var source = new Source();
        source.Line("// Which of this application's entities appear in somebody's calendar.");
        source.Line("//");
        source.Line("// WHAT THIS COVERS. Every date in THIS application that belongs to the person");
        source.Line("// reading it — the entities listed below, filtered to them, at /api/me/calendar");
        source.Line("// and on the Calendar screen. The owner filter is set on the server and cannot be");
        source.Line("// asked for in a query string, so this is your calendar and nobody else's.");
        source.Line("//");
        source.Line("// WHAT IT DOES NOT. One feed across several applications. Cordango Platform puts");
        source.Line("// every app a person can reach into a single calendar; this is one application, so");
        source.Line("// there is nothing here to span. If you deploy more of these side by side, each");
        source.Line("// carries its own calendar and the person reads two.");
        source.Line("//");
        source.Line("// Generated from the definition's `calendar` flags. Regenerating overwrites it.");
        source.Line();
        source.Line("using Cordango.Standalone.Calendar;");
        source.Line("using Cordango.Standalone.Conditions;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Calendar;");
        source.Line();
        source.Line("/// <summary>Which entities appear in people's calendars, and where each one keeps");
        source.Line("/// its dates, its owner and its title. Resolved from the definition at build time.</summary>");
        source.Open("public static class AppCalendar");
        source.Line("public static readonly CalendarDescriptor Descriptor = new(");
        source.Indent();
        source.Line("[");
        source.Indent();

        foreach (var entity in sources) Write(source, entity);

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Calendar/AppCalendar.cs", source.ToString());
    }

    private static void Write(Source source, EntityModel entity)
    {
        var cal = Binding(entity);

        var who = AppModel.Str(cal["who"])!;
        var via = AppModel.Str(cal["whoVia"]);
        var whoEntity = AppModel.Str(cal["whoEntity"]);

        // `whoEntity` is the parent when a hop is in play, and this entity otherwise. The runtime
        // needs the parent's KEY to read it through its own gateway — a hop that skipped that check
        // would be a way to read a project's people through a milestone's calendar.
        var parent = via is null ? null : whoEntity;

        var start = AppModel.Str(cal["start"])!;
        var end = AppModel.Str(cal["end"]);
        var status = AppModel.Str(cal["statusField"]);

        source.Line("new CalendarSource(");
        source.Indent();
        source.Line($"Entity: {Quote(entity.Key)},");
        source.Line($"Start: {Quote(start)},");
        source.Line($"End: {Quote(end)},");
        source.Line($"Who: {Quote(who)},");
        source.Line($"Via: {Quote(via)},");
        source.Line($"Parent: {Quote(parent)},");
        source.Line($"Title: {Quote(AppModel.Str(cal["title"]) ?? entity.DisplayField ?? "id")},");
        source.Line($"AllDay: {(AppModel.Bool(cal["allDay"]) ? "true" : "false")},");
        source.Line($"StatusField: {Quote(status)},");
        WriteOptions(source, entity, status);

        // A hideWhen the emitter cannot write must not become an application without one: the records
        // the definition keeps OUT of the calendar would appear in it. The generator reports that as
        // CORD2306 and the build stops, so what reaches here is only what came out.
        source.Line($"HideWhen: {ConditionEmitter.Emit(cal["hideWhen"])},");

        source.Line($"StartWritable: {(Writable(entity, start) ? "true" : "false")},");
        source.Line($"EndWritable: {(end is not null && Writable(entity, end) ? "true" : "false")}),");
        source.Outdent();
    }

    /// <summary>The status field's options, which are where an entry's colour and its process phase
    /// come from. Without them a pending request and an approved one are drawn identically, which is
    /// the whole reason the binding names a status field at all.</summary>
    private static void WriteOptions(Source source, EntityModel entity, string? status)
    {
        var options = status is null
            ? []
            : AppModel.Arr(entity.Field(status)?.Json["options"]).OfType<JsonObject>().ToList();

        if (options.Count == 0)
        {
            source.Line("Options: [],");
            return;
        }

        source.Line("Options:");
        source.Line("[");
        source.Indent();
        foreach (var option in options)
        {
            var value = AppModel.Str(option["value"]);
            if (value is null) continue;

            source.Line($"new CalendarOption({Quote(value)}, "
                + $"{Quote(AppModel.Str(option["label"]) ?? value)}, "
                + $"{Quote(AppModel.Str(option["color"]))}, "
                + $"{Quote(AppModel.Str(option["phase"]))}),");
        }

        source.Outdent();
        source.Line("],");
    }

    /// <summary>Whether a role could ever move this date. A computed, read-only or system field is
    /// one the calendar shows and cannot drag, and saying so per field is what stops a surface
    /// offering a gesture the API would refuse.</summary>
    private static bool Writable(EntityModel entity, string fieldKey) =>
        entity.Field(fieldKey) is { } field
        && !AppModel.Bool(field.Json["system"])
        && !AppModel.Bool(field.Json["readOnly"])
        && field.Json["computed"] is not JsonObject;

    /// <summary>The binding the compiler stamped in. Never null for an entity <see cref="AppModel
    /// .Calendars"/> handed over — that property is what decided it was resolved.</summary>
    private static JsonObject Binding(EntityModel entity) => (JsonObject)entity.Json["calendar"]!;

    private static string Quote(string? value) => value is null ? "null" : Naming.Literal(value);
}
