// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;
using Cordango.SourceGen.DotNet;
using Cordango.SourceGen.DotNet.Emit;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The entity-level <c>calendar</c> flag, generated rather than refused.
///
/// <para><b>What changed and why it is worth a test.</b> The flag used to land as CORD2309 for every
/// entity that carried one, on the grounds that a calendar spanning every app in a workspace is a
/// platform surface. Half of that was right. The flag says two things — "these records belong in the
/// responsible person's calendar" and "alongside every other app's dates" — and only the second is
/// out of reach for one application. So the first is emitted in full, and CORD2309 is left for the
/// case that really is broken: a flag that reached the generator with no binding on it.</para>
/// </summary>
public class CalendarEmitTests
{
    [Fact]
    public void The_flag_alone_becomes_a_source_the_runtime_can_read()
    {
        var descriptor = Descriptor(App(TimeOff));

        Assert.Contains("Entity: \"time_off\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("Start: \"start_date\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("End: \"end_date\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("Who: \"requested_by\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("Title: \"reason\"", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_options_travel_with_it_so_a_pending_entry_is_drawn_differently()
    {
        var descriptor = Descriptor(App(TimeOff));

        Assert.Contains("StatusField: \"status\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("new CalendarOption(\"pending\", \"Pending\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("new CalendarOption(\"approved\", \"Approved\"", descriptor, StringComparison.Ordinal);
    }

    /// <summary>The hop that puts a milestone in the calendar of whoever leads its project. What the
    /// runtime needs on top of the reference is the PARENT'S KEY — it reads the parent through the
    /// parent's own gateway, so a role that may not read projects gets no milestones through the back
    /// door of a calendar.</summary>
    [Fact]
    public void An_owned_entity_carries_the_hop_and_the_parent_it_hops_to()
    {
        var descriptor = Descriptor(App(ProjectAndMilestone));

        Assert.Contains("Entity: \"milestone\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("Via: \"project\"", descriptor, StringComparison.Ordinal);
        Assert.Contains("Parent: \"project\"", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_with_no_calendar_gets_no_file_and_no_endpoint()
    {
        var app = App(TimeOffWithoutTheFlag);

        Assert.Null(CalendarEmitter.Emit(app));
        Assert.DoesNotContain("AddAppCalendar", BackendEmitter.Setup(app).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_endpoint_is_registered_where_there_is_one()
    {
        var setup = BackendEmitter.Setup(App(TimeOff)).Content;

        Assert.Contains("services.AddAppCalendar(Calendar.AppCalendar.Descriptor);", setup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_is_routed_only_where_something_has_dates()
    {
        var withDates = Router(App(TimeOff));
        Assert.Contains("name: 'calendar'", withDates, StringComparison.Ordinal);
        Assert.Contains("import CalendarView", withDates, StringComparison.Ordinal);

        // A link to an unregistered route does not render as a dead link: router.resolve THROWS from
        // inside the render and takes the page with it. So the navigation asks `app.calendar`, and
        // the answer has to be there.
        var without = Router(App(TimeOffWithoutTheFlag));
        Assert.DoesNotContain("name: 'calendar'", without, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_told_whether_there_is_a_calendar_at_all()
    {
        Assert.Contains("\"calendar\": true", AppModule(App(TimeOff)), StringComparison.Ordinal);
        Assert.Contains("\"calendar\": false", AppModule(App(TimeOffWithoutTheFlag)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolved_flag_is_no_longer_reported_as_a_gap()
    {
        var warnings = Build(TimeOff).Warnings;

        Assert.DoesNotContain(warnings, d => d.Code == NotYetCodes.Calendar);
    }

    /// <summary>An UNRESOLVED flag still is. The compiler turns `calendar: true` into a binding and
    /// refuses the build when it cannot, so a bare flag here means something bypassed that — and
    /// emitting nothing would put the records in nobody's calendar without a word.</summary>
    [Fact]
    public void An_unresolved_flag_is_still_reported()
    {
        var manifest = Manifest(TimeOff);
        var entity = manifest["entities"]!.AsArray().OfType<JsonObject>()
            .First(e => (string?)e["key"] == "time_off");
        entity["calendar"] = true;

        var result = new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));

        Assert.Contains(result.Warnings,
            d => d.Code == NotYetCodes.Calendar
                && d.Message.Contains("unresolved", StringComparison.Ordinal));
    }

    private const string Person =
        """{"key":"requested_by","label":"Requested by","type":"reference","targetApp":"platform","targetEntity":"person"}""";

    private const string TimeOff = $$"""
    [ { "key":"time_off","label":"Time off","labelPlural":"Time off","displayField":"reason","calendar":true,
        "fields":[
          {"key":"reason","label":"Reason","type":"text"},
          {"key":"start_date","label":"Start","type":"date","role":"start"},
          {"key":"end_date","label":"End","type":"date","role":"due"},
          {"key":"status","label":"Status","type":"select","role":"status",
           "options":[{"value":"pending","label":"Pending"},{"value":"approved","label":"Approved"}]},
          {{Person}}
        ] } ]
    """;

    private const string TimeOffWithoutTheFlag = $$"""
    [ { "key":"time_off","label":"Time off","labelPlural":"Time off","displayField":"reason",
        "fields":[
          {"key":"reason","label":"Reason","type":"text"},
          {"key":"start_date","label":"Start","type":"date","role":"start"},
          {{Person}}
        ] } ]
    """;

    private const string ProjectAndMilestone = $$"""
    [ { "key":"project","label":"Project","labelPlural":"Projects","displayField":"title",
        "fields":[
          {"key":"title","label":"Title","type":"text"},
          {{Person}}
        ] },
      { "key":"milestone","label":"Milestone","labelPlural":"Milestones","displayField":"title",
        "calendar":true, "ownedBy":{"parent":"project","via":"project"},
        "fields":[
          {"key":"project","label":"Project","type":"reference","targetEntity":"project"},
          {"key":"title","label":"Title","type":"text"},
          {"key":"due_on","label":"Due","type":"date","role":"due"}
        ] } ]
    """;

    private static readonly DateTimeOffset At = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Manifest(string entities) =>
        AppCompiler.Compile((JsonObject)JsonNode.Parse($$"""
        { "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0","entities":{{entities}} }
        """)!, "app1", At);

    private static AppModel App(string entities)
    {
        var manifest = Manifest(entities);
        return AppModel.From(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")));
    }

    private static string Descriptor(AppModel app) =>
        CalendarEmitter.Emit(app)?.Content
        ?? throw new InvalidOperationException("No calendar was emitted.");

    private static string Router(AppModel app) =>
        Web(app).Single(f => f.RelativePath == "web/src/router.js").Content;

    private static string AppModule(AppModel app) =>
        Web(app).Single(f => f.RelativePath == "web/src/app.js").Content;

    private static IReadOnlyList<GeneratedFile> Web(AppModel app) =>
        WebEmitter.Emit(app, allowPartial: true, new DotNetVueGenerator().Capabilities).Files;

    private static GenerateResult Build(string entities)
    {
        var manifest = Manifest(entities);
        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }
}
