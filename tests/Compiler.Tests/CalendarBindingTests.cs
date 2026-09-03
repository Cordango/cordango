// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The <c>calendar</c> opt-in: one flag, everything else worked out from what the entity already
/// declares — and a refusal, by name, wherever it cannot be.
///
/// <para>The refusals are the point. A flag that resolved to nothing would put the record in
/// nobody's calendar and say so nowhere, which is the shape of every silent breakage the emitter has
/// produced. The Gate and the compiler run the same resolver, so a definition that passes the check
/// is one the build can resolve.</para>
/// </summary>
public class CalendarBindingTests
{
    private static JsonObject Doc(string entities) => (JsonObject)JsonNode.Parse($$"""
    { "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0", "entities":{{entities}} }
    """)!;

    private static readonly DateTimeOffset At = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject CalendarOf(JsonObject doc, string entityKey) =>
        (JsonObject)AppCompiler.Compile(doc, "app1", At)["entities"]!.AsArray()
            .OfType<JsonObject>().First(e => (string?)e["key"] == entityKey)["calendar"]!;

    private static JsonObject FieldOf(JsonObject doc, string entityKey, string fieldKey) =>
        (JsonObject)AppCompiler.Compile(doc, "app1", At)["entities"]!.AsArray()
            .OfType<JsonObject>().First(e => (string?)e["key"] == entityKey)["fields"]!.AsArray()
            .OfType<JsonObject>().First(f => (string?)f["key"] == fieldKey);

    private static JsonObject? MaybeCalendarOf(JsonObject doc, string entityKey) =>
        AppCompiler.Compile(doc, "app1", At)["entities"]!.AsArray()
            .OfType<JsonObject>().First(e => (string?)e["key"] == entityKey)["calendar"] as JsonObject;

    private const string Person = """{"key":"requested_by","label":"Requested by","type":"reference","targetApp":"platform","targetEntity":"person"}""";

    private static string TimeOff(string calendar) => $$"""
    [ { "key":"time_off","label":"Time off","displayField":"reason","calendar":{{calendar}},
        "fields":[
          {"key":"reason","label":"Reason","type":"text"},
          {"key":"start_date","label":"Start","type":"date","role":"start"},
          {"key":"end_date","label":"End","type":"date","role":"due"},
          {"key":"status","label":"Status","type":"select","role":"status",
           "options":[{"value":"pending","label":"Pending"},{"value":"approved","label":"Approved"}]},
          {{Person}}
        ] } ]
    """;

    [Fact]
    public void The_flag_alone_resolves_start_end_who_title_and_state()
    {
        var cal = CalendarOf(Doc(TimeOff("true")), "time_off");

        Assert.Equal("start_date", (string?)cal["start"]);
        Assert.Equal("end_date", (string?)cal["end"]);
        Assert.Equal("requested_by", (string?)cal["who"]);
        Assert.Equal("time_off", (string?)cal["whoEntity"]);
        Assert.Null(cal["whoVia"]);
        Assert.Equal("reason", (string?)cal["title"]);
        Assert.Equal("status", (string?)cal["statusField"]);
        Assert.True((bool?)cal["allDay"]);
    }

    [Fact]
    public void An_authored_key_is_never_overwritten_by_derivation()
    {
        var cal = CalendarOf(Doc(TimeOff("""{"title":"{{reason}} away","allDay":false}""")), "time_off");

        Assert.Equal("{{reason}} away", (string?)cal["title"]);
        Assert.False((bool?)cal["allDay"]);
        Assert.Equal("start_date", (string?)cal["start"]);
        Assert.Equal("requested_by", (string?)cal["who"]);
    }

    [Fact]
    public void An_absent_flag_leaves_the_entity_off_the_calendar()
    {
        Assert.Null(MaybeCalendarOf(Doc(TimeOff("false")), "time_off"));

        var noFlag = """
        [ { "key":"invoice","label":"Invoice","displayField":"number",
            "fields":[{"key":"number","label":"Number","type":"text"},
                      {"key":"paid_on","label":"Paid on","type":"date"}] } ]
        """;
        Assert.Null(MaybeCalendarOf(Doc(noFlag), "invoice"));
        Assert.Empty(Gate.SemanticErrors(Doc(noFlag)));
    }

    [Fact]
    public void A_datetime_start_is_not_an_all_day_entry()
    {
        var doc = Doc("""
        [ { "key":"interview","label":"Interview","displayField":"candidate",
            "calendar":true,
            "fields":[
              {"key":"candidate","label":"Candidate","type":"text"},
              {"key":"scheduled_at","label":"Scheduled","type":"datetime"},
              {"key":"interviewed_by","label":"Interviewer","type":"reference","targetApp":"platform","targetEntity":"person"}
            ] } ]
        """);

        var cal = CalendarOf(doc, "interview");
        Assert.Equal("scheduled_at", (string?)cal["start"]);
        Assert.False((bool?)cal["allDay"]);
        Assert.Null(cal["end"]);
    }

    private static string MilestoneAndProject(string milestoneCalendar) => $$"""
    [ { "key":"project","label":"Project","displayField":"name",
        "fields":[
          {"key":"name","label":"Name","type":"text"},
          {"key":"lead","label":"Lead","type":"reference","targetApp":"platform","targetEntity":"person"}
        ] },
      { "key":"milestone","label":"Milestone","displayField":"title",
        "calendar":{{milestoneCalendar}},
        "ownedBy":{"parent":"project","via":"project"},
        "fields":[
          {"key":"title","label":"Title","type":"text"},
          {"key":"due_date","label":"Due","type":"date","role":"due"},
          {"key":"project","label":"Project","type":"reference","targetEntity":"project"}
        ] } ]
    """;

    [Fact]
    public void A_milestone_finds_its_person_through_ownedBy_to_the_project_lead()
    {
        var doc = Doc(MilestoneAndProject("true"));
        Assert.Empty(Gate.SemanticErrors(doc));

        var cal = CalendarOf(doc, "milestone");
        Assert.Equal("due_date", (string?)cal["start"]);
        Assert.Null(cal["end"]);
        Assert.Equal("lead", (string?)cal["who"]);
        Assert.Equal("project", (string?)cal["whoVia"]);
        Assert.Equal("project", (string?)cal["whoEntity"]);
    }

    [Fact]
    public void A_due_date_start_does_not_become_a_zero_length_span()
    {
        var cal = CalendarOf(Doc(MilestoneAndProject("true")), "milestone");
        Assert.Equal((string?)cal["start"], "due_date");
        Assert.Null(cal["end"]);
    }

    [Fact]
    public void Several_unroled_dates_are_refused_and_all_of_them_are_named()
    {
        var doc = Doc($$"""
        [ { "key":"claim","label":"Claim","displayField":"title","calendar":true,
            "fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"incident_on","label":"Incident","type":"date"},
              {"key":"filed_on","label":"Filed","type":"date"},
              {"key":"paid_on","label":"Paid","type":"date"},
              {{Person}}
            ] } ]
        """);

        var errors = Gate.SemanticErrors(doc);
        var error = Assert.Single(errors.Where(e => e.Contains("several date fields", StringComparison.Ordinal)));
        Assert.Contains("incident_on", error, StringComparison.Ordinal);
        Assert.Contains("filed_on", error, StringComparison.Ordinal);
        Assert.Contains("paid_on", error, StringComparison.Ordinal);
        Assert.Contains("role:'start'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_the_start_settles_what_several_dates_could_not()
    {
        var doc = Doc($$"""
        [ { "key":"claim","label":"Claim","displayField":"title",
            "calendar":{"start":"incident_on"},
            "fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"incident_on","label":"Incident","type":"date"},
              {"key":"filed_on","label":"Filed","type":"date"},
              {{Person}}
            ] } ]
        """);

        Assert.Empty(Gate.SemanticErrors(doc));
        Assert.Equal("incident_on", (string?)CalendarOf(doc, "claim")["start"]);
    }

    [Fact]
    public void An_entity_with_no_date_at_all_is_refused()
    {
        var doc = Doc($$"""
        [ { "key":"note","label":"Note","displayField":"body","calendar":true,
            "fields":[{"key":"body","label":"Body","type":"longtext"},{{Person}}] } ]
        """);

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("no date field", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entity_nobody_is_responsible_for_is_refused_and_told_why()
    {
        var doc = Doc("""
        [ { "key":"holiday","label":"Holiday","displayField":"name","calendar":true,
            "fields":[
              {"key":"name","label":"Name","type":"text"},
              {"key":"observed_on","label":"Observed","type":"date","role":"start"}
            ] } ]
        """);

        var error = Assert.Single(Gate.SemanticErrors(doc)
            .Where(e => e.Contains("references a person", StringComparison.Ordinal)));
        Assert.Contains("holiday", error, StringComparison.Ordinal);
        Assert.Contains("cannot land in anybody's calendar", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_with_no_person_either_is_named_in_the_refusal()
    {
        var doc = Doc("""
        [ { "key":"project","label":"Project","displayField":"name",
            "fields":[{"key":"name","label":"Name","type":"text"}] },
          { "key":"milestone","label":"Milestone","displayField":"title","calendar":true,
            "ownedBy":{"parent":"project","via":"project"},
            "fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"due_date","label":"Due","type":"date","role":"due"},
              {"key":"project","label":"Project","type":"reference","targetEntity":"project"}
            ] } ]
        """);

        var error = Assert.Single(Gate.SemanticErrors(doc)
            .Where(e => e.Contains("references a person", StringComparison.Ordinal)));
        Assert.Contains("its parent 'project'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_people_are_settled_by_precedence_not_by_declaration_order()
    {
        var doc = Doc("""
        [ { "key":"ticket","label":"Ticket","displayField":"subject","calendar":true,
            "fields":[
              {"key":"subject","label":"Subject","type":"text"},
              {"key":"due_date","label":"Due","type":"date","role":"due"},
              {"key":"submitted_by","label":"Submitted by","type":"reference","targetApp":"platform","targetEntity":"person"},
              {"key":"assignee","label":"Assignee","type":"reference","targetApp":"platform","targetEntity":"person"}
            ] } ]
        """);

        Assert.Empty(Gate.SemanticErrors(doc));
        Assert.Equal("assignee", (string?)CalendarOf(doc, "ticket")["who"]);
    }

    [Fact]
    public void A_who_that_is_not_a_person_reference_is_refused()
    {
        var doc = Doc("""
        [ { "key":"visit","label":"Visit","displayField":"title",
            "calendar":{"who":"site"},
            "fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"visit_on","label":"Visit on","type":"date","role":"start"},
              {"key":"site","label":"Site","type":"text"},
              {"key":"owner","label":"Owner","type":"reference","targetApp":"platform","targetEntity":"person"}
            ] } ]
        """);

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("calendar.who 'site'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hideWhen_on_a_field_the_entity_does_not_have_is_refused()
    {
        var doc = Doc(TimeOff("""{"hideWhen":{"field":"cancelled","operator":"eq","value":true}}"""));

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("calendar.hideWhen on 'cancelled'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hideWhen_that_holds_up_is_carried_into_the_manifest()
    {
        var doc = Doc(TimeOff("""{"hideWhen":{"field":"status","operator":"eq","value":"declined"}}"""));

        Assert.Empty(Gate.SemanticErrors(doc));
        var hide = Assert.IsType<JsonObject>(CalendarOf(doc, "time_off")["hideWhen"]);
        Assert.Equal("status", (string?)hide["field"]);
    }

    [Fact]
    public void A_nested_hideWhen_is_walked_to_its_leaves()
    {
        var doc = Doc(TimeOff("""
        {"hideWhen":{"any":[{"field":"status","operator":"eq","value":"declined"},
                            {"field":"withdrawn","operator":"eq","value":true}]}}
        """));

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("calendar.hideWhen on 'withdrawn'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_system_timestamps_every_entity_gains_do_not_count_as_its_dates()
    {
        // created_at/updated_at/deleted_at are datetimes the compiler injects into every entity. If
        // they counted, "the single date field" would never be single and this would refuse the
        // entity that motivated the whole feature.
        var doc = Doc($$"""
        [ { "key":"visit","label":"Visit","displayField":"title","calendar":true,
            "fields":[
              {"key":"title","label":"Title","type":"text"},
              {"key":"visit_on","label":"Visit on","type":"date"},
              {{Person}}
            ] } ]
        """);

        Assert.Empty(Gate.SemanticErrors(doc));
        Assert.Equal("visit_on", (string?)CalendarOf(doc, "visit")["start"]);
    }

    [Fact]
    public void The_field_the_calendar_reads_as_who_stays_fillable_despite_its_audit_name()
    {
        var f = FieldOf(Doc(TimeOff("true")), "time_off", "requested_by");

        Assert.Equal("currentUser", (string?)f["auto"]);
        Assert.Null(f["readOnly"]);
        Assert.Null(f["hideOnCreate"]);
    }

    [Fact]
    public void The_same_field_on_an_entity_off_the_calendar_stays_locked_to_the_acting_user()
    {
        var f = FieldOf(Doc(TimeOff("false")), "time_off", "requested_by");

        Assert.Equal("currentUser", (string?)f["auto"]);
        Assert.True((bool?)f["readOnly"]);
    }

    [Fact]
    public void Compiling_twice_produces_the_same_binding()
    {
        var once = CalendarOf(Doc(TimeOff("true")), "time_off");
        var twice = CalendarOf(Doc(TimeOff("true")), "time_off");
        Assert.Equal(once.ToJsonString(), twice.ToJsonString());
    }

    [Fact]
    public void An_end_equal_to_the_start_is_refused_rather_than_drawn_as_nothing()
    {
        var doc = Doc(TimeOff("""{"start":"start_date","end":"start_date"}"""));

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("the same field as its", StringComparison.Ordinal));
    }
}
