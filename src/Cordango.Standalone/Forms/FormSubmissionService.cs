// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Workflows;

namespace Cordango.Standalone.Forms;

/// <summary>What a submission did, or the reasons it did not.</summary>
/// <param name="Ok">False when nothing was filed and the errors say why.</param>
/// <param name="Errors">Every reason at once — a form rejected one question at a time is a form
/// somebody submits four times.</param>
/// <param name="ResponseId">The stored submission, present even when the projection failed.</param>
/// <param name="TargetEntity">What was filed, when the template targets something.</param>
/// <param name="Record">The record that was filed. Never handed to an anonymous caller.</param>
public sealed record SubmissionResult(
    bool Ok,
    IReadOnlyList<string>? Errors = null,
    string? ResponseId = null,
    string? TargetEntity = null,
    JsonObject? Record = null);

/// <summary>
/// Take a form, and turn the submission into a real record in the application.
///
/// <para>A survey collects answers; an INTAKE form files something. The difference is one step: after
/// the response and its answers are stored, the template's <c>targetEntity</c> says what to create,
/// each question's <c>mapsTo</c> says which field it fills, and the template's own routing fields
/// carry an assignee, a source, a campaign onto the new record. The engine copies values; it has no
/// idea what a lead is, which is what makes the same machinery serve a helpdesk.</para>
///
/// <para><b>Server-authoritative, and that is the point rather than a detail.</b> Writing this in the
/// browser would mean a submitter needed create rights on the response AND the answer AND the target
/// entity — which a stranger filling in a public form has none of, and which a colleague filing a
/// ticket should not need either. Every write here goes through <see cref="IEntityWriter"/>, the same
/// seam a workflow effect writes through: below the permission façade, above the store, so the record
/// it files runs its validation, its computed fields and its create hooks exactly as a hand-typed one
/// would.</para>
///
/// <para>Deliberately mirrors the platform's service of the same name, including the order of the
/// three writes and the decision to validate everything before performing any of them. Two
/// implementations that accepted different submissions would be two contracts wearing one name.</para>
/// </summary>
public sealed class FormSubmissionService
{
    /// <summary>Longest a single short-text answer may be. Generous: a bound on abuse, not an opinion
    /// about how much somebody may have to say.</summary>
    private const int MaxShortText = 500;

    private const int MaxLongText = 10_000;

    private readonly FormsDescriptor _forms;
    private readonly IEnumerable<IEntityWriter> _writers;

    public FormSubmissionService(FormsDescriptor forms, IEnumerable<IEntityWriter> writers)
    {
        _forms = forms;
        _writers = writers;
    }

    private IEntityWriter? Writer(string entity) =>
        _writers.FirstOrDefault(w => string.Equals(w.Entity, entity, StringComparison.Ordinal));

    /// <summary>The questions of one form, in the order they are asked.</summary>
    public async Task<IReadOnlyList<JsonObject>> QuestionsAsync(string templateId, CancellationToken ct)
    {
        if (Writer(_forms.QuestionEntity) is not { } questions) return [];

        var rows = await questions.WhereAsync(
            [new RecordFilter(_forms.Question.Template, "eq", templateId)], ct);

        var ordered = _forms.Question.Order is { } order
            ? rows.OrderBy(r => Num(r[order]) ?? 0).ToList()
            : [.. rows];
        return ordered;
    }

    /// <param name="project">
    /// False holds the submission at the response: the answers are stored and the record the template
    /// targets is NOT created.
    ///
    /// <para>That is the whole of what a confirmation step is — a form whose author asked for a
    /// verified address must not put anything in a real person's queue until the address has been
    /// proven, and the answers themselves are still worth keeping the moment they arrive.</para>
    /// </param>
    public async Task<SubmissionResult> SubmitAsync(
        string templateId, IReadOnlyDictionary<string, JsonNode?> answers, CancellationToken ct,
        bool project = true)
    {
        var templates = Writer(_forms.TemplateEntity);
        var responses = Writer(_forms.ResponseEntity);
        var answerRows = Writer(_forms.AnswerEntity);
        if (templates is null || responses is null || answerRows is null)
            return new SubmissionResult(false, ["This application cannot collect form submissions."]);

        if (await templates.FindAsync(templateId, ct) is not { } template)
            return new SubmissionResult(false, ["That form no longer exists."]);

        var questions = await QuestionsAsync(templateId, ct);
        var byId = questions.Where(q => Str(q["id"]) is not null).ToDictionary(q => Str(q["id"])!);

        // Checked BEFORE anything is written. Until a stranger could post here, `required` lived in
        // the browser and an answer's type was never checked at all — survivable while every
        // submitter was a signed-in colleague looking at the form, and not survivable now. Checking
        // first is also what makes a rejected submission leave nothing behind.
        if (Validate(questions, answers) is { Count: > 0 } bad)
            return new SubmissionResult(false, bad);

        // 1. The response.
        var responsePayload = new JsonObject();
        if (_forms.ResponseTemplate is { } responseRef) responsePayload[responseRef] = templateId;
        var response = await responses.CreateAsync(responsePayload, ct);
        if (Str(response["id"]) is not { } responseId)
            return new SubmissionResult(false, ["The submission could not be saved."]);

        // 2. One answer per answered question. An answer naming a question that is not on this form
        //    is dropped rather than refused: a stale browser tab posting a question the author has
        //    since deleted should file what it can.
        foreach (var (questionId, value) in answers)
        {
            if (!byId.ContainsKey(questionId) || IsBlank(value)) continue;
            var payload = new JsonObject();
            if (_forms.AnswerResponse is { } ar) payload[ar] = responseId;
            if (_forms.AnswerQuestion is { } aq) payload[aq] = questionId;
            if (_forms.AnswerValue is { } av) payload[av] = value?.DeepClone();
            await answerRows.CreateAsync(payload, ct);
        }

        if (!project) return new SubmissionResult(true, ResponseId: responseId);
        return await ProjectAsync(template, templateId, responseId, answers, byId, ct);
    }

    /// <summary>
    /// Turn a stored response into the record its template targets.
    ///
    /// <para>Shared by the immediate path and the confirmed one on purpose: a submission that filed
    /// straight away and one that waited for a clicked link must produce the SAME record.</para>
    /// </summary>
    private async Task<SubmissionResult> ProjectAsync(
        JsonObject template, string templateId, string responseId,
        IReadOnlyDictionary<string, JsonNode?> answers, IReadOnlyDictionary<string, JsonObject> byId,
        CancellationToken ct)
    {
        var targetKey = _forms.TargetEntityField is { } tk ? Str(template[tk]) : null;
        // A plain survey stops here, and that is a success: the answers are the artifact.
        if (string.IsNullOrWhiteSpace(targetKey)) return new SubmissionResult(true, ResponseId: responseId);

        if (Writer(targetKey!) is not { } target)
            return new SubmissionResult(true, ResponseId: responseId);

        var record = new JsonObject();

        // Routing and defaults from the TEMPLATE: 'route_assigned_to' declaring mapsTo 'assigned_to'
        // means every lead from this form starts assigned there.
        foreach (var route in _forms.Routing)
            if (!IsBlank(template[route.Source]))
                record[route.Target] = template[route.Source]!.DeepClone();

        // Per-question mapping: the answers the author wanted ON the record rather than only inside
        // the response.
        if (_forms.Question.MapsTo is { } mapsTo)
            foreach (var (questionId, value) in answers)
            {
                if (IsBlank(value) || !byId.TryGetValue(questionId, out var question)) continue;
                if (Str(question[mapsTo]) is { } destination) record[destination] = value?.DeepClone();
            }

        // Link the record back to the submission, so its detail can show the answers.
        if (_forms.TargetBackReferences.TryGetValue(targetKey!, out var backReference))
            record[backReference] = responseId;

        try
        {
            var made = await target.CreateAsync(record, ct);
            return new SubmissionResult(true, ResponseId: responseId, TargetEntity: targetKey, Record: made);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // The answers are kept. A form whose questions do not cover a required field of what it
            // files is the author's mistake, and losing what somebody typed is not the way to
            // report it.
            return new SubmissionResult(false, [e.Message], ResponseId: responseId);
        }
    }

    /// <summary>Finish a submission that was held back for a confirmed address.</summary>
    public async Task<SubmissionResult> ProjectStoredAsync(
        string templateId, string responseId, CancellationToken ct)
    {
        if (Writer(_forms.TemplateEntity) is not { } templates
            || await templates.FindAsync(templateId, ct) is not { } template)
            return new SubmissionResult(false, ["That form no longer exists."]);

        var questions = await QuestionsAsync(templateId, ct);
        var byId = questions.Where(q => Str(q["id"]) is not null).ToDictionary(q => Str(q["id"])!);

        // Re-read from the rows rather than carried in memory: the two requests are minutes or hours
        // apart and may not be the same process. The response IS the durable artifact.
        var answers = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (Writer(_forms.AnswerEntity) is { } answerRows && _forms.AnswerResponse is { } ar)
        {
            var rows = await answerRows.WhereAsync([new RecordFilter(ar, "eq", responseId)], ct);
            foreach (var row in rows)
            {
                if (_forms.AnswerQuestion is not { } aq || Str(row[aq]) is not { } questionId) continue;
                answers[questionId] = _forms.AnswerValue is { } av ? row[av]?.DeepClone() : null;
            }
        }

        return await ProjectAsync(template, templateId, responseId, answers, byId, ct);
    }

    /// <summary>The address the submitter gave, from whichever question the author MARKED as asking
    /// for it. Read through the role rather than by looking for an answer containing an '@': a form
    /// can ask for several addresses and only the author knows which one is the person filling
    /// it in.</summary>
    public async Task<string?> RespondentEmailAsync(
        string templateId, IReadOnlyDictionary<string, JsonNode?> answers, CancellationToken ct)
    {
        if (_forms.Question.RespondentEmail is not { } mark) return null;
        foreach (var question in await QuestionsAsync(templateId, ct))
        {
            if (question[mark]?.GetValueKind() != System.Text.Json.JsonValueKind.True) continue;
            if (Str(question["id"]) is not { } id || !answers.TryGetValue(id, out var value)) continue;
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains('@')) return text;
        }
        return null;
    }

    private List<string> Validate(
        IReadOnlyList<JsonObject> questions, IReadOnlyDictionary<string, JsonNode?> answers)
    {
        // A bound on the PARSE, not on the content. Unknown ids are dropped when the answers are
        // written, but a caller can still post a hundred thousand of them and make the server look
        // each one up.
        if (answers.Count > questions.Count)
            return ["That submission carries more answers than the form has questions."];

        var errors = new List<string>();
        var textKey = _forms.Question.Text;

        foreach (var question in questions)
        {
            var id = Str(question["id"]);
            if (id is null) continue;
            var label = (textKey is null ? null : Str(question[textKey])) ?? "A question";
            answers.TryGetValue(id, out var value);

            var required = _forms.Question.Required is { } requiredKey
                && question[requiredKey]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
            if (required && IsBlank(value)) { errors.Add($"'{label}' is required."); continue; }
            if (IsBlank(value)) continue;

            var kind = _forms.Question.AnswerType is { } typeKey ? Str(question[typeKey]) : null;
            var options = _forms.Question.Options is { } optionsKey ? question[optionsKey] : null;
            if (!Fits(kind, value!, options)) errors.Add($"'{label}' was not answered in a way this question accepts.");
        }

        return errors;
    }

    /// <summary>Does this value fit the kind of question that was asked. The same eight kinds the
    /// browser renders, checked again here because the browser is not where a stranger's submission
    /// is decided.</summary>
    private static bool Fits(string? kind, JsonNode value, JsonNode? options)
    {
        var valueKind = value.GetValueKind();

        List<string> Choices() =>
            (options as JsonArray ?? []).Select(o => o is JsonObject option
                ? Str(option["value"]) ?? Str(option["label"])
                : (o as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                .OfType<string>().ToList();

        switch (kind)
        {
            case "yes_no":
                return valueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
            case "number":
                return valueKind == System.Text.Json.JsonValueKind.Number;
            case "scale":
                return Num(value) is { } n && n % 1 == 0 && n is >= 1 and <= 10;
            case "date":
                return valueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(Str(value), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out _);
            case "short_text":
                return valueKind == System.Text.Json.JsonValueKind.String && (Str(value)?.Length ?? 0) <= MaxShortText;
            case "long_text":
                return valueKind == System.Text.Json.JsonValueKind.String && (Str(value)?.Length ?? 0) <= MaxLongText;
            case "single_choice":
            {
                if (valueKind != System.Text.Json.JsonValueKind.String) return false;
                var choices = Choices();
                // A question with no choices declared cannot contradict itself; its author has more
                // to fix than this submission does.
                return choices.Count == 0 || choices.Contains(Str(value)!, StringComparer.Ordinal);
            }
            case "multi_choice":
            {
                if (value is not JsonArray array || array.Count > 100) return false;
                var choices = Choices();
                return array.All(x => x?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && (choices.Count == 0 || choices.Contains(Str(x)!, StringComparer.Ordinal)));
            }
            default:
                return true;
        }
    }

    private static bool IsBlank(JsonNode? value) => value is null
        || (value is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
        || (value is JsonArray a && a.Count == 0);

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Through the text, not through GetValue&lt;double&gt;(): a JsonNode holding an Int64 —
    /// which is what every whole number arrives as — throws on that cast.</summary>
    private static double? Num(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
