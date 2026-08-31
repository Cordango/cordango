// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// The Forms archetype, resolved at BUILD time and written down.
///
/// <para>The platform reads these roles out of a manifest on every request. A generated application
/// has no manifest, and giving it one would mean shipping the whole definition to the server so it
/// could go looking for four entity roles in it. Everything the runtime needs is knowable here, so
/// this emits it — the same argument <c>AppDescriptors</c> makes for emitting a copy delegate rather
/// than reflecting over property names.</para>
///
/// <para>Emits nothing at all when the application has no forms. The two controllers are gated on the
/// descriptor being registered, so an application without one has no form endpoints rather than
/// endpoints that answer "not configured".</para>
/// </summary>
public static class FormsEmitter
{
    public static GeneratedFile? Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var template = ByRole(app, "formTemplate");
        var question = ByRole(app, "formField");
        var response = ByRole(app, "formResponse");
        var answer = ByRole(app, "formAnswer");

        // All four or none. Three of them is an incomplete archetype the Gate already refuses, and
        // guessing the fourth here would put a half-wired endpoint on the internet.
        if (template is null || question is null || response is null || answer is null) return null;

        var source = new Source();
        source.Line("using Cordango.Standalone.Forms;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Forms;");
        source.Line();
        source.Line("/// <summary>Which entity and which field of it plays each part in the forms");
        source.Line("/// archetype. Resolved from the definition's roles at build time.</summary>");
        source.Open("public static class AppForms");
        source.Line("public static readonly FormsDescriptor Catalogue = new(");
        source.Indent();
        source.Line($"TemplateEntity: {Quote(template.Key)},");
        source.Line($"QuestionEntity: {Quote(question.Key)},");
        source.Line($"ResponseEntity: {Quote(response.Key)},");
        source.Line($"AnswerEntity: {Quote(answer.Key)},");

        source.Line("Question: new FormQuestionFields(");
        source.Indent();
        source.Line($"Template: {Quote(RefTo(question, template.Key) ?? "form")},");
        source.Line($"Text: {Quote(DisplayOrText(question))},");
        source.Line($"AnswerType: {Quote(FieldByRole(question, "answerType"))},");
        source.Line($"Options: {Quote(FieldByRole(question, "answerOptions"))},");
        source.Line($"Required: {Quote(FieldByRole(question, "answerRequired"))},");
        source.Line($"Order: {Quote(FieldByRole(question, "order"))},");
        source.Line($"MapsTo: {Quote(FieldByRole(question, "mapsTo"))},");
        source.Line($"RespondentEmail: {Quote(FieldByRole(question, "respondentEmail"))}),");
        source.Outdent();

        source.Line($"ResponseTemplate: {Quote(RefTo(response, template.Key))},");
        source.Line($"AnswerResponse: {Quote(RefTo(answer, response.Key))},");
        source.Line($"AnswerQuestion: {Quote(RefTo(answer, question.Key))},");
        source.Line($"AnswerValue: {Quote(FieldByRole(answer, "answerValue"))},");
        source.Line($"TargetEntityField: {Quote(FieldByRole(template, "targetEntity"))},");
        source.Line($"ConfirmMode: {Quote(FieldByRole(template, "confirmMode"))},");
        source.Line($"PublicShare: {Quote(FieldByRole(template, "publicShare"))},");
        source.Line($"PublicToken: {Quote(FieldByRole(template, "publicToken"))},");

        // The template's own routing: what every record filed by a given form starts with.
        var routing = template.AuthoredFields
            .Where(f => AppModel.Str(f.Json["mapsTo"]) is { Length: > 0 })
            .ToList();
        if (routing.Count == 0)
        {
            source.Line("Routing: [],");
        }
        else
        {
            source.Line("Routing:");
            source.Line("[");
            source.Indent();
            foreach (var field in routing)
                source.Line($"new FormRouting({Quote(field.Key)}, {Quote(AppModel.Str(field.Json["mapsTo"]))}),");
            source.Outdent();
            source.Line("],");
        }

        // Where a filed record keeps its link back to the submission, per entity that declares one.
        // Only the answers block needs it, so an entity without one costs nothing else.
        var backReferences = app.Entities
            .Select(e => (Entity: e.Key, Field: RefTo(e, response.Key)))
            .Where(x => x.Field is not null && !string.Equals(x.Entity, answer.Key, StringComparison.Ordinal))
            .ToList();
        if (backReferences.Count == 0)
        {
            source.Line("TargetBackReferences: new Dictionary<string, string>());");
        }
        else
        {
            source.Line("TargetBackReferences: new Dictionary<string, string>");
            source.Line("{");
            source.Indent();
            foreach (var (entity, field) in backReferences)
                source.Line($"[{Quote(entity)}] = {Quote(field)},");
            source.Outdent();
            source.Line("});");
        }

        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Forms/AppForms.cs", source.ToString());
    }

    /// <summary>True when this application has the archetype at all, so the caller can decide whether
    /// to register the module.</summary>
    public static bool HasForms(AppModel app) =>
        ByRole(app, "formTemplate") is not null && ByRole(app, "formField") is not null
        && ByRole(app, "formResponse") is not null && ByRole(app, "formAnswer") is not null;

    private static EntityModel? ByRole(AppModel app, string role) =>
        app.Entities.FirstOrDefault(e => AppModel.Str(e.Json["role"]) == role);

    private static string? FieldByRole(EntityModel entity, string role) =>
        entity.AuthoredFields.FirstOrDefault(f => AppModel.Str(f.Json["role"]) == role)?.Key;

    /// <summary>The reference on <paramref name="entity"/> pointing at <paramref name="target"/>.</summary>
    private static string? RefTo(EntityModel entity, string target) =>
        entity.AuthoredFields.FirstOrDefault(f =>
            f.Type == "reference" && f.TargetApp is null && f.TargetEntity == target)?.Key;

    /// <summary>The question as somebody READS it: the entity's display field, or the first text it
    /// carries. A question with neither is one the author has more to fix about than this.</summary>
    private static string? DisplayOrText(EntityModel entity) =>
        AppModel.Str(entity.Json["displayField"])
        ?? entity.AuthoredFields.FirstOrDefault(f => f.Type is "text" or "longtext")?.Key;

    private static string Quote(string? value) =>
        value is null ? "null" : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
