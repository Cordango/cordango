// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Forms;

/// <summary>Answers keyed by question id, and nothing else.</summary>
public sealed record FormSubmission(Dictionary<string, JsonNode?>? Answers);

/// <summary>
/// A stranger's submission: the answers, the proof of work, and the honeypot.
///
/// <para><b>The DTO's shape is the security boundary.</b> There is no entity here, no field name and
/// no workspace — a caller can say what they answered and nothing about where it goes. Everything
/// else is derived server-side from the template the token resolved to. The moment a public endpoint
/// accepts "which field should I write", it is an arbitrary write primitive with a rate limit in
/// front of it.</para>
/// </summary>
/// <param name="Website">The honeypot. Rendered off-screen, never announced, and therefore only ever
/// filled in by something that reads the DOM and answers every input it finds. Named `website`
/// because that is a name naive bots look for.</param>
public sealed record PublicFormSubmission(
    Dictionary<string, JsonNode?>? Answers,
    string? ChallengeToken,
    string? Solution,
    string? Website = null);

public static class FormsModule
{
    /// <summary>
    /// Wire the forms archetype up, when the application has one.
    ///
    /// <para>Called by generated code with the descriptor the generator resolved. An application
    /// without the forms plugin never calls it, and its two controllers then answer 404 like any
    /// other address that is not there — which is why they are gated on the descriptor being
    /// registered rather than on a feature flag somebody could set inconsistently.</para>
    /// </summary>
    public static IServiceCollection AddForms(this IServiceCollection services, FormsDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton(descriptor);
        services.AddSingleton<ProofOfWork>();
        services.AddScoped<FormSubmissionService>();
        return services;
    }
}

/// <summary>
/// Filing a form from INSIDE the application, as somebody who is signed in.
///
/// <para>One call. Writing the response and then an answer per question from the browser would mean a
/// submitter needed create rights on every forms entity, and a failure halfway through would leave
/// orphaned rows nobody cleaned up.</para>
/// </summary>
[ApiController]
[Route("api/forms")]
public sealed class AppFormsController : ControllerBase
{
    private readonly FormSubmissionService _forms;

    public AppFormsController(FormSubmissionService forms) => _forms = forms;

    [HttpGet("{templateId}/questions")]
    public async Task<IActionResult> Questions(string templateId, CancellationToken ct) =>
        Ok(new { questions = await _forms.QuestionsAsync(templateId, ct) });

    [HttpPost("{templateId}/submit")]
    public async Task<IActionResult> Submit(
        string templateId, [FromBody] FormSubmission? body, CancellationToken ct)
    {
        var answers = body?.Answers ?? [];
        var result = await _forms.SubmitAsync(templateId, answers, ct);

        if (!result.Ok)
            return UnprocessableEntity(new { error = result.Errors?.FirstOrDefault(), errors = result.Errors });

        return Ok(new { ok = true, entity = result.TargetEntity, record = result.Record });
    }
}

/// <summary>
/// A published form, filled in by somebody with no account.
///
/// <para><b>The shape of this endpoint IS its permission model.</b> There is no "public" role to
/// grant, because an author could grant one broadly and quietly turn this into a general data API.
/// Instead the caller posts answers to ONE resolved template and nothing else — it cannot name an
/// entity, a field, or a record. Which response entity, which answer entity, which record gets
/// created and which of its fields get filled are all derived server-side from the template row.
/// There is nothing left for a permission check to protect.</para>
///
/// <para>What publishing a form therefore means, stated plainly because an author has to understand
/// it before they flip the switch: <b>the internet may create exactly one response, its answers, and
/// one target record with exactly the fields that form maps.</b> That is the feature, not a leak.</para>
///
/// <para>Every failure — unknown token, unpublished form, a form that no longer exists — is one flat
/// 404 with the same body. Telling a script which of the three it hit is telling it what to try
/// next.</para>
/// </summary>
[ApiController]
[Route("api/public/forms")]
[AllowAnonymous]
public sealed class PublicFormsController : ControllerBase
{
    /// <summary>The proof-of-work surface name, so a challenge minted for a form cannot be spent
    /// somewhere else if the two addresses ever collide.</summary>
    private const string Surface = "form";

    private readonly FormsDescriptor _descriptor;
    private readonly FormSubmissionService _forms;
    private readonly IEnumerable<IEntityWriter> _writers;
    private readonly ProofOfWork _pow;

    public PublicFormsController(
        FormsDescriptor descriptor, FormSubmissionService forms,
        IEnumerable<IEntityWriter> writers, ProofOfWork pow)
    {
        _descriptor = descriptor;
        _forms = forms;
        _writers = writers;
        _pow = pow;
    }

    private IActionResult NoSuchForm() => NotFound(new { error = "This form is not available." });

    /// <summary>
    /// Token → the published template it addresses, or null.
    ///
    /// <para>The single choke point every route here calls first. The record's own
    /// <c>publicShare</c> flag is the authority, checked after the token matched — a token with the
    /// switch off has no more right to be served than one that never existed.</para>
    /// </summary>
    private async Task<JsonObject?> FindAsync(string token, CancellationToken ct)
    {
        if (_descriptor.PublicToken is not { } tokenField || _descriptor.PublicShare is not { } shareField)
            return null;
        if (string.IsNullOrWhiteSpace(token)) return null;

        var templates = _writers.FirstOrDefault(w =>
            string.Equals(w.Entity, _descriptor.TemplateEntity, StringComparison.Ordinal));
        if (templates is null) return null;

        var rows = await templates.WhereAsync([new RecordFilter(tokenField, "eq", token)], ct);
        if (rows.FirstOrDefault() is not { } template) return null;
        return template[shareField]?.GetValueKind() == JsonValueKind.True ? template : null;
    }

    /// <summary>
    /// The form itself: its name, and its questions in the order they are asked.
    ///
    /// <para><b>The field ROLES are the allow-list.</b> A question projects as
    /// {id, text, answerType, required, options} read through the roles the author declared — so a
    /// question entity that also carries an internal scoring weight or a reviewer's note does not put
    /// those on the public internet just because somebody added a column.</para>
    /// </summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } template) return NoSuchForm();

        var q = _descriptor.Question;
        var questions = await _forms.QuestionsAsync(Str(template["id"])!, ct);

        return Ok(new
        {
            name = Name(template),
            questions = questions.Select(row => new
            {
                id = Str(row["id"]),
                text = q.Text is null ? null : Str(row[q.Text]),
                answerType = q.AnswerType is null ? null : Str(row[q.AnswerType]),
                required = q.Required is not null && row[q.Required]?.GetValueKind() == JsonValueKind.True,
                options = q.Options is null ? null : row[q.Options]?.DeepClone(),
            }),
        });
    }

    [HttpGet("{token}/challenge")]
    public async Task<IActionResult> Challenge(string token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is null) return NoSuchForm();
        var challenge = _pow.Issue(Surface, token);
        return Ok(new { token = challenge.Token, difficulty = challenge.Difficulty, expiresAt = challenge.ExpiresAt });
    }

    [HttpPost("{token}")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Submit(
        string token, [FromBody] PublicFormSubmission? body, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } template) return NoSuchForm();

        // Three cheap filters, one answer. None of them stops somebody who is trying, and none of
        // them pretends to — together they turn away the drive-by spam that is what a public form
        // actually receives. ONE message for every way of failing: telling a script WHICH check it
        // tripped is telling it how to pass.
        if (!string.IsNullOrWhiteSpace(body?.Website)
            || _pow.Verify(Surface, token, body?.ChallengeToken, body?.Solution, ProofOfWork.MinAge)
               != ProofOfWork.Verdict.Ok)
            return BadRequest(new { code = "forms.challenge_invalid", error = "That submission could not be accepted. Please try again." });

        var answers = body?.Answers ?? [];
        var result = await _forms.SubmitAsync(Str(template["id"])!, answers, ct);

        if (!result.Ok)
            return UnprocessableEntity(new { error = result.Errors?.FirstOrDefault(), errors = result.Errors });

        // Nothing about the record it filed goes back over the wire. The submitter has no account and
        // no reach into the application; handing them an id and a row would be the one place this
        // endpoint leaked the application's own data back out.
        return Ok(new { ok = true, status = "received" });
    }

    private string? Name(JsonObject template)
    {
        // The form's own display value, whatever the author called that field. Falls back to nothing
        // rather than to a key: a heading reading "intake_form" helps nobody.
        foreach (var candidate in new[] { "name", "title", "label" })
            if (Str(template[candidate]) is { Length: > 0 } value) return value;
        return null;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
