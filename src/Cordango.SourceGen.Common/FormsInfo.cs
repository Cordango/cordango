// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.SourceGen.Common;

/// <summary>
/// The Forms archetype's entity and field keys, resolved once from the definition's roles.
///
/// <para>Read by both halves of the generator and for the same reason: the backend emits it into a
/// descriptor the runtime holds, and the web emitter puts the same keys on the components as props.
/// Neither side looks a role up at run time, and both get their answers from here so the two cannot
/// drift into disagreeing about which column holds a question's text.</para>
///
/// <para>Null on an application with no forms, which is what every caller checks first.</para>
/// </summary>
public sealed record FormsInfo(
    string TemplateEntity,
    string QuestionEntity,
    string ResponseEntity,
    string AnswerEntity,
    string? QuestionText,
    string? QuestionOrder,
    string? AnswerResponse,
    string? AnswerQuestion,
    string? AnswerValue,
    IReadOnlyDictionary<string, string> BackReferences)
{
    /// <summary>Resolve it, or null when the application does not have all four roles. Three of them
    /// is an incomplete archetype the Gate already refuses, and guessing the fourth here would wire a
    /// surface to an entity nobody nominated.</summary>
    public static FormsInfo? Resolve(IReadOnlyList<EntityModel> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var template = ByRole(entities, "formTemplate");
        var question = ByRole(entities, "formField");
        var response = ByRole(entities, "formResponse");
        var answer = ByRole(entities, "formAnswer");
        if (template is null || question is null || response is null || answer is null) return null;

        // Which field on each OTHER entity points back at a submission. Only the answers block needs
        // it, so an entity without one costs nothing else.
        var backReferences = entities
            .Where(e => !string.Equals(e.Key, answer.Key, StringComparison.Ordinal))
            .Select(e => (e.Key, Field: RefTo(e, response.Key)))
            .Where(x => x.Field is not null)
            .ToDictionary(x => x.Key, x => x.Field!, StringComparer.Ordinal);

        return new FormsInfo(
            template.Key, question.Key, response.Key, answer.Key,
            QuestionText: AppModel.Str(question.Json["displayField"])
                ?? question.AuthoredFields.FirstOrDefault(f => f.Type is "text" or "longtext")?.Key,
            QuestionOrder: FieldByRole(question, "order"),
            AnswerResponse: RefTo(answer, response.Key),
            AnswerQuestion: RefTo(answer, question.Key),
            AnswerValue: FieldByRole(answer, "answerValue"),
            BackReferences: backReferences);
    }

    private static EntityModel? ByRole(IReadOnlyList<EntityModel> entities, string role) =>
        entities.FirstOrDefault(e => AppModel.Str(e.Json["role"]) == role);

    private static string? FieldByRole(EntityModel entity, string role) =>
        entity.AuthoredFields.FirstOrDefault(f => AppModel.Str(f.Json["role"]) == role)?.Key;

    private static string? RefTo(EntityModel entity, string target) =>
        entity.AuthoredFields.FirstOrDefault(f =>
            f.Type == "reference" && f.TargetApp is null && f.TargetEntity == target)?.Key;
}
