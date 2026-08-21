// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The INTAKE half of the Forms archetype: a form whose submission is projected into a real record,
/// and the two surfaces that expose it (<c>intake</c> — pick a form and file something;
/// <c>answers</c> — what the requester said, shown on the record it filed).
///
/// <para>Only the SHAPE is checkable here, and that split is the point: which entity a template
/// targets and which field each question fills are runtime data an ordinary user configures in the
/// designer, so the gate validates the declarations and the submit endpoint validates the data.
/// These tests pin the line between the two.</para>
/// </summary>
public class FormIntakeGateTests
{
    /// <summary>A helpdesk-shaped intake app: the four Forms roles, plus the ticket a submission
    /// files. `form.route_to` carries routing; `question.fills` carries per-answer mapping.</summary>
    private static JsonObject IntakeApp() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "desk", "name": "Desk", "version": "1.0.0",
      "plugins": [{ "id": "forms" }],
      "entities": [
        { "key": "form", "label": "Form", "role": "formTemplate", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "creates", "label": "Creates", "type": "text", "role": "targetEntity" },
            { "key": "route_to", "label": "Route to", "type": "reference", "targetApp": "platform",
              "targetEntity": "person", "onDelete": "setNull", "mapsTo": "assigned_to" } ] },
        { "key": "question", "label": "Question", "role": "formField", "displayField": "text",
          "fields": [
            { "key": "text", "label": "Text", "type": "text" },
            { "key": "form", "label": "Form", "type": "reference", "targetEntity": "form" },
            { "key": "kind", "label": "Kind", "type": "select", "role": "answerType",
              "options": [{ "value": "short_text", "label": "Short text" }] },
            { "key": "fills", "label": "Fills", "type": "text", "role": "mapsTo" } ] },
        { "key": "submission", "label": "Submission", "role": "formResponse", "displayField": "id",
          "fields": [{ "key": "form", "label": "Form", "type": "reference", "targetEntity": "form" }] },
        { "key": "answer", "label": "Answer", "role": "formAnswer", "displayField": "id",
          "fields": [
            { "key": "submission", "label": "Submission", "type": "reference", "targetEntity": "submission" },
            { "key": "question", "label": "Question", "type": "reference", "targetEntity": "question" },
            { "key": "value", "label": "Value", "type": "json", "role": "answerValue" } ] },
        { "key": "ticket", "label": "Ticket", "displayField": "subject",
          "fields": [
            { "key": "subject", "label": "Subject", "type": "text" },
            { "key": "assigned_to", "label": "Assigned to", "type": "reference", "targetApp": "platform",
              "targetEntity": "person", "onDelete": "setNull" },
            { "key": "from_form", "label": "Filed from", "type": "reference", "targetEntity": "submission" } ] }
      ],
      "views": [{ "key": "t", "label": "Tickets", "type": "table", "entity": "ticket" }],
      "roles": [{ "key": "admin", "name": "Admin",
                  "grants": [{ "entity": "*", "create": true, "read": true, "update": true, "delete": true }] }]
    }
    """)!;

    private static JsonObject Entity(JsonObject doc, string key) =>
        ((JsonArray)doc["entities"]!).OfType<JsonObject>().First(e => (string?)e["key"] == key);

    private static JsonObject Field(JsonObject doc, string entity, string field) =>
        ((JsonArray)Entity(doc, entity)["fields"]!).OfType<JsonObject>().First(f => (string?)f["key"] == field);

    private static void SetPages(JsonObject doc, string json) => doc["pages"] = JsonNode.Parse(json);
    private static void SetTicketDetail(JsonObject doc, string blocks) =>
        Entity(doc, "ticket")["detail"] = JsonNode.Parse($$"""{ "blocks": {{blocks}} }""");

    [Fact]
    public void A_complete_intake_app_passes() => Assert.Empty(Gate.Validate(IntakeApp()));

    // --- the declarations ---------------------------------------------------------------------

    [Fact]
    public void TargetEntity_belongs_on_the_template()
    {
        var doc = IntakeApp();
        Field(doc, "ticket", "subject")["role"] = "targetEntity";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'ticket.subject' has role 'targetEntity'") && e.Contains("belongs on a formTemplate"));
    }

    [Fact]
    public void TargetEntity_holds_an_entity_key_so_it_must_be_text()
    {
        var doc = IntakeApp();
        Field(doc, "form", "creates")["type"] = "integer";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'form.creates' has role 'targetEntity' so it must be a 'text' field"));
    }

    [Fact]
    public void MapsTo_belongs_on_the_template_that_has_something_to_map_onto()
    {
        var doc = IntakeApp();
        Field(doc, "ticket", "subject")["mapsTo"] = "something";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'ticket.subject' declares 'mapsTo'") && e.Contains("only means something on a formTemplate"));
    }

    [Fact]
    public void Routing_without_a_target_is_dead_weight_and_says_so()
    {
        var doc = IntakeApp();
        Field(doc, "form", "creates").Remove("role");   // the template can no longer name what it creates
        var errors = Gate.SemanticErrors(doc);
        Assert.Contains(errors, e => e.Contains("routes fields with 'mapsTo'") && e.Contains("nothing to route them onto"));
        Assert.Contains(errors, e => e.Contains("formField 'question' maps answers onto a target entity"));
    }

    [Fact]
    public void Question_level_mapsTo_belongs_on_the_question()
    {
        var doc = IntakeApp();
        Field(doc, "form", "name")["role"] = "mapsTo";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'form.name' has role 'mapsTo'") && e.Contains("belongs on a formField"));
    }

    // --- the answers block (record binding) ----------------------------------------------------

    [Fact]
    public void Answers_block_resolves_its_link_to_the_submission()
    {
        var doc = IntakeApp();
        SetTicketDetail(doc, """[{ "kind": "answers", "via": "from_form" }]""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Answers_block_finds_the_single_link_without_being_told()
    {
        var doc = IntakeApp();
        SetTicketDetail(doc, """[{ "kind": "answers" }]""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Answers_block_via_must_point_at_the_submission()
    {
        var doc = IntakeApp();
        SetTicketDetail(doc, """[{ "kind": "answers", "via": "assigned_to" }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("answers block via 'ticket.assigned_to' must be a reference to the formResponse entity"));
    }

    [Fact]
    public void Answers_block_needs_the_record_to_link_to_a_submission_at_all()
    {
        var doc = IntakeApp();
        // A ticket nobody can trace back to a form has no answers to show, and saying so at author
        // time beats an empty panel nobody can explain.
        ((JsonArray)Entity(doc, "ticket")["fields"]!).RemoveAt(2);
        SetTicketDetail(doc, """[{ "kind": "answers" }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("answers block needs 'ticket' to have a reference to the formResponse entity"));
    }

    [Fact]
    public void Two_links_to_the_submission_are_ambiguous_until_one_is_named()
    {
        var doc = IntakeApp();
        ((JsonArray)Entity(doc, "ticket")["fields"]!).Add(JsonNode.Parse(
            """{ "key": "also_from", "label": "Also from", "type": "reference", "targetEntity": "submission" }"""));
        SetTicketDetail(doc, """[{ "kind": "answers" }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("more than one reference to the formResponse entity"));

        SetTicketDetail(doc, """[{ "kind": "answers", "via": "from_form" }]""");
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Answers_is_a_record_block_not_a_page_block()
    {
        var doc = IntakeApp();
        SetPages(doc, """[{ "key": "p", "label": "P", "entity": "ticket", "blocks": [{ "kind": "answers" }] }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("block kind 'answers' is only valid in a record detail"));
    }

    // --- the intake block (collection binding) -------------------------------------------------

    [Fact]
    public void Intake_block_offers_the_apps_forms()
    {
        var doc = IntakeApp();
        SetPages(doc, """
          [{ "key": "file", "label": "File a request", "entity": "form",
             "blocks": [{ "kind": "intake", "entity": "form", "label": "What do you need?" }] }]
          """);
        Assert.Empty(Gate.Validate(doc));
    }

    [Fact]
    public void Intake_block_entity_must_be_a_form()
    {
        var doc = IntakeApp();
        SetPages(doc, """
          [{ "key": "file", "label": "File", "entity": "ticket", "blocks": [{ "kind": "intake", "entity": "ticket" }] }]
          """);
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("intake block entity 'ticket' must have role 'formTemplate'"));
    }

    [Fact]
    public void Intake_filters_resolve_against_the_form_entity()
    {
        var doc = IntakeApp();
        SetPages(doc, """
          [{ "key": "file", "label": "File", "entity": "form", "blocks": [
             { "kind": "intake", "filters": [{ "field": "retired", "operator": "eq", "value": true }] }] }]
          """);
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'retired' is not a field of 'form'"));
    }

    [Fact]
    public void Intake_is_a_page_block_not_a_record_block()
    {
        var doc = IntakeApp();
        SetTicketDetail(doc, """[{ "kind": "intake" }]""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("block kind 'intake' is only valid on pages"));
    }
}
