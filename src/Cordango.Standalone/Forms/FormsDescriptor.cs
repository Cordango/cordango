// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Forms;

/// <summary>
/// Which field of a question holds what. Every one of these is a FIELD ROLE the author declared, read
/// once at build time so the runtime never has to look for it.
/// </summary>
/// <param name="Template">The reference back to the form this question belongs to.</param>
/// <param name="Text">The question itself, as somebody reads it.</param>
/// <param name="AnswerType">role:'answerType' — the select naming what kind of answer this is.</param>
/// <param name="Options">role:'answerOptions' — the choices a choice question offers, or null.</param>
/// <param name="Required">role:'answerRequired' — whether it must be answered, or null when the
/// author never declared it, in which case nothing is required and that is a fact about the
/// definition rather than a bug here.</param>
/// <param name="Order">role:'order' — the integer the questions are asked in.</param>
/// <param name="MapsTo">role:'mapsTo' — the field of the TARGET entity this answer fills.</param>
/// <param name="RespondentEmail">role:'respondentEmail' — marks the question that asks for the
/// submitter's own address, which is the one a confirmation link could be sent to.</param>
public sealed record FormQuestionFields(
    string Template,
    string? Text,
    string? AnswerType,
    string? Options,
    string? Required,
    string? Order,
    string? MapsTo,
    string? RespondentEmail);

/// <summary>One field of the TEMPLATE that routes a value onto every record the form files.</summary>
/// <param name="Source">The field on the template the author fills in.</param>
/// <param name="Target">The field of the target entity it is copied into.</param>
public sealed record FormRouting(string Source, string Target);

/// <summary>
/// The Forms archetype, resolved.
///
/// <para><b>Emitted, not discovered.</b> The platform reads these roles out of a manifest at request
/// time; a generated application has no manifest, and giving it one would mean shipping the whole
/// definition to the server so it could look for four entity roles in it. The generator already
/// knows every answer at build time, so it writes them down — the same argument
/// <see cref="Data.RecordDescriptor{T}"/> makes for emitting a copy delegate instead of reflecting
/// over property names.</para>
///
/// <para>Absent entirely when the application does not enable the forms plugin, which is what the
/// controllers check before answering at all.</para>
/// </summary>
/// <param name="TemplateEntity">The entity with role:'formTemplate'.</param>
/// <param name="QuestionEntity">role:'formField'.</param>
/// <param name="ResponseEntity">role:'formResponse'.</param>
/// <param name="AnswerEntity">role:'formAnswer'.</param>
/// <param name="Question">Where a question keeps each of its parts.</param>
/// <param name="ResponseTemplate">The reference on a response pointing at its template.</param>
/// <param name="AnswerResponse">The reference on an answer pointing at its response.</param>
/// <param name="AnswerQuestion">The reference on an answer pointing at its question.</param>
/// <param name="AnswerValue">role:'answerValue' — the json column the submitted value lands in.</param>
/// <param name="TargetEntityField">role:'targetEntity' — the template field naming what a submission
/// CREATES. Null on an application whose forms are surveys and file nothing.</param>
/// <param name="ConfirmMode">role:'confirmMode', or null.</param>
/// <param name="PublicShare">role:'publicShare' — the switch that serves this form to strangers.</param>
/// <param name="PublicToken">role:'publicToken' — the generated address it is served at.</param>
/// <param name="Routing">The template's own mapsTo fields: assignee, source, campaign — what every
/// record from this form starts with.</param>
/// <param name="TargetBackReferences">Per target entity, the field on it that points back at the
/// response, so a filed record can show the answers it came from. Empty where the author did not
/// declare one, which only costs the answers block.</param>
public sealed record FormsDescriptor(
    string TemplateEntity,
    string QuestionEntity,
    string ResponseEntity,
    string AnswerEntity,
    FormQuestionFields Question,
    string? ResponseTemplate,
    string? AnswerResponse,
    string? AnswerQuestion,
    string? AnswerValue,
    string? TargetEntityField,
    string? ConfirmMode,
    string? PublicShare,
    string? PublicToken,
    IReadOnlyList<FormRouting> Routing,
    IReadOnlyDictionary<string, string> TargetBackReferences);
