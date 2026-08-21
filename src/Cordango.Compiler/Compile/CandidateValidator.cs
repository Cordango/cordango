// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compile;

/// <summary>
/// What one <c>submit_candidate</c> produced. <see cref="Hash"/> is non-null <b>only</b> when
/// <see cref="Valid"/> — the design loop's <c>finish</c> tool accepts a hash and refuses anything it
/// did not issue, so a hash that names a definition which failed somewhere must not exist to be
/// quoted back.
/// </summary>
/// <param name="Definition">The definition AFTER deterministic fills — the artifact
/// <see cref="Hash"/> covers and the only one a caller may persist or publish.</param>
/// <param name="Fills">One note per hole the finalizer closed. Surfaced, never silent: a fill means
/// the model left something out, which is worth seeing even when the run succeeds.</param>
/// <param name="Coherent">The document passed <c>Gate.Validate</c> and COMPILED — it holds together —
/// even if it is not yet a finished application.
///
/// <para><b>Only the smoke checks distinguish this from <paramref name="Valid"/>.</b> They are the ones
/// about the finished product rather than the document's internal consistency: no pages, nowhere to
/// land. An incremental author hits them constantly and legitimately — a domain authored before its
/// screens has no pages yet and is not therefore wrong.</para>
///
/// <para>It exists so a stateful authoring session can tell "not finished" from "broken" without a
/// second validator and without reading error strings. <b><paramref name="Valid"/> still governs
/// publication, and this never yields a hash</b> — see <c>Incomplete</c>.</para></param>
public sealed record CandidateOutcome(
    bool Valid,
    string? Hash,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Fills,
    JsonNode? Definition,
    JsonObject? Manifest,
    bool Coherent = false)
{
    internal static CandidateOutcome Rejected(IReadOnlyList<string> errors, JsonNode? definition = null,
        IReadOnlyList<string>? fills = null) =>
        new(false, null, errors, fills ?? [], definition, null);

    /// <summary>Gate-clean and compiling, but not a finished application.
    ///
    /// <para><b>No hash, deliberately.</b> A hash is a licence to publish and is issued in exactly one
    /// place — the success path of <see cref="CandidateValidator.Run"/>, after the smoke checks. An
    /// incomplete document must be editable and unpublishable at the same time, and withholding the
    /// hash is what makes the second half true by construction rather than by a rule someone
    /// remembers.</para></summary>
    internal static CandidateOutcome Incomplete(IReadOnlyList<string> errors, JsonNode definition,
        IReadOnlyList<string> fills, JsonObject manifest) =>
        new(false, null, errors, fills, definition, manifest, Coherent: true);
}

/// <summary>
/// The whole correctness pipeline behind the design agent's <c>submit_candidate</c> tool, in one
/// call the model cannot take apart.
///
/// <code>
/// parse → Gate.Validate → deterministic fills → Gate.Validate again
///       → AppCompiler.Compile → smoke checks → hash
/// </code>
///
/// <para><b>Why it is one function.</b> The generation flow this replaces gave the model a sequence
/// of steps and trusted it to run them in order. It did not, and could not be made to: a pass that
/// declared its own output good was the only judge of that output. Here the backend runs every step
/// on every submission, so "the model forgot to validate" stops being a reachable state.</para>
///
/// <para><b>Fills run INSIDE validation.</b> <see cref="DesignDefaults"/> sits between the two gate
/// passes, never after — nothing may mutate the definition once <see cref="CandidateOutcome.Hash"/>
/// covers it, or an approval would bind a document that is not the one that gets published. The
/// second pass exists because a fill edits the block tree, and an unvalidated edit is exactly the
/// class of bug this pipeline is here to make impossible.</para>
///
/// <para><b>A failure is a failure.</b> Anything reported here is structural: invalid JSON, a gate
/// error, a compiler throw, a missing create path. None of it is an "open issue" — open issues are
/// honest platform limitations a person should decide about, and letting a broken definition wear
/// that label is how a run ends with an app nobody can use and a list that says it went fine.</para>
///
/// <para><b>Not checked here:</b> "the preview loads with synthetic data" from the plan's smoke
/// list. It needs the data plane and a seeded store, so it belongs to the preview endpoint rather
/// than to a pure function. Everything else on that list is either below or already a
/// <see cref="Gate"/> rule (reference resolution, process-state reachability, roles).</para>
/// </summary>
public static class CandidateValidator
{
    /// <param name="definitionJson">Raw tool argument — assumed hostile, parsed here rather than by
    /// the caller so a parse failure is reported in the same shape as every other failure.</param>
    /// <param name="appId">The draft's app id, from the JOB ROW. Never from model output: a
    /// definition that could name its own app id could compile itself into someone else's app.</param>
    public static CandidateOutcome Run(string? definitionJson, string appId, DateTimeOffset builtAt)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(definitionJson ?? "");
        }
        catch (JsonException ex)
        {
            return CandidateOutcome.Rejected([$"PARSE: {ex.Message}"]);
        }
        if (parsed is null) return CandidateOutcome.Rejected(["PARSE: the candidate is empty"]);
        return Run(parsed, appId, builtAt);
    }

    public static CandidateOutcome Run(JsonNode definition, string appId, DateTimeOffset builtAt)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // The caller's node is never touched: a rejected candidate must leave whatever it was handed
        // exactly as it was, and everything below edits in place.
        var doc = definition.DeepClone();

        // `Normalizer.Repair` is the one entry point for model output heading to the gate: it
        // un-stringifies sections a model double-encoded, and lowers any namespaced authoring kind
        // (`layout.section`) to the canonical one the gate speaks. The design agent's tool schema is
        // the canonical one, so the lowering is a safety net rather than a translation step — and it
        // is a no-op on a document that already speaks canonical, which is why the reference apps go
        // through this identical path.
        var recoveries = new List<Normalizer.LenientRecovery>();
        doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode(), recoveries) ?? doc;
        var repairs = recoveries
            .Select(r => $"repaired a double-encoded section at '{r.Path}' (expected {r.Expected}, "
                       + $"tolerated the trailing '{r.Suffix}')")
            .ToList();

        // The contract version is the PLATFORM's fact, not the model's. Stamped before the gate runs
        // rather than asked for, because a model has no way to know it and every wrong guess is a
        // submission spent on a constant — or worse, a document that validates, compiles, saves, and
        // then cannot be opened, which is what "1.0" did (see AppSchemaVersion).
        if (AppSchemaVersion.Stamp(doc) is { } mislabelled)
            repairs.Add($"set schemaVersion to {AppSchemaVersion.Current} "
                      + $"(the candidate declared '{mislabelled}')");

        var errors = Gate.Validate(doc);
        if (errors.Count > 0) return CandidateOutcome.Rejected(errors, doc, repairs);

        // ---- deterministic completion, inside the validated window -------------------------------
        // `plan: null` — there is no DesignPlan in this architecture; the planner belonged to the
        // multi-pass flow. EnsurePrimaryViewIsPlaced is a no-op without one (it only ever acted on a
        // planner's stated intent), so what survives here is the create-path floor, which is exactly
        // the fill that is safe without a plan to read intent from.
        var fills = repairs.Concat(doc is JsonObject obj ? DesignDefaults.Apply(doc, plan: null, obj) : []).ToList();

        errors = Gate.Validate(doc);
        if (errors.Count > 0)
        {
            // A fill broke the document. That is a bug HERE, not in the candidate, and it must never
            // read as the model's fault in a repair prompt.
            return CandidateOutcome.Rejected(
                errors.Select(e => $"INTERNAL (a deterministic fill invalidated the definition): {e}").ToList(),
                doc, fills);
        }

        JsonObject manifest;
        try
        {
            manifest = AppCompiler.Compile(doc, appId, builtAt);
        }
        catch (Exception ex)
        {
            // Broad on purpose: the compiler is not written to be fed model output directly, and
            // whatever it throws is a rejection of this candidate, not a crash of the run.
            return CandidateOutcome.Rejected([$"COMPILE: {ex.GetType().Name}: {ex.Message}"], doc, fills);
        }

        // Everything above this line is COHERENCE: the document is internally consistent, survives the
        // gate's 351 rules, and compiles. Everything below is whether it is a finished application.
        // The distinction is reported rather than collapsed, because an incremental author needs to be
        // told "not yet" without being told "wrong" — and because the alternative, letting a session
        // guess from error strings, puts a second interpretation of correctness in the codebase.
        var smoke = SmokeErrors(doc, manifest);
        if (smoke.Count > 0) return CandidateOutcome.Incomplete(smoke, doc, fills, manifest);

        return new CandidateOutcome(true, DefinitionHash.Of(doc), [], fills, doc, manifest, Coherent: true);
    }

    /// <summary>
    /// What a gate-clean, compiling definition can still get wrong: an app that is not an app.
    ///
    /// <para>Every check here is about the finished product rather than the document's internal
    /// consistency, which is why none of them belong in <see cref="Gate"/>. They replace the
    /// generated behavioural scenarios: deterministic, instant, and they cannot report success on
    /// output they never looked at.</para>
    /// </summary>
    private static List<string> SmokeErrors(JsonNode definition, JsonObject manifest)
    {
        var errors = new List<string>();

        // 1. The model designed an app at all.
        //
        // Checked on the DEFINITION, not the manifest: the compiler synthesizes a page per entity and
        // a table view per page when they are missing, so a definition with no pages compiles into a
        // plausible-looking manifest. That fallback exists for domain-only output from the old flow;
        // accepting it here would let a candidate skip the entire design half of the job and pass.
        var entities = (definition as JsonObject)?["entities"] as JsonArray;
        if (entities is not { Count: > 0 })
            errors.Add("SMOKE: the definition declares no entities — there is nothing for the app to hold");
        if ((definition as JsonObject)?["pages"] is not JsonArray { Count: > 0 })
            errors.Add("SMOKE: the definition declares no pages — the app has no screens, and the "
                     + "compiler's synthesized page-per-entity fallback is not a design");
        if (errors.Count > 0) return errors;   // the rest read a document that has the shape to read

        // 2. Somewhere to land. Pages grouped `config` are collapsed into the shell's Configuration
        //    section, so an app whose every page is config opens onto nothing.
        var pages = manifest["pages"] as JsonArray ?? [];
        if (!pages.OfType<JsonObject>().Any(p => Str(p, "group") != "config"))
            errors.Add("SMOKE: every page is in the Configuration group — the app has no landing page");

        // 3. Data a user can put in. The finalizer fills this where it can; what reaches here is a
        //    hole it declined to close (nothing shows the entity, or no form exists to open).
        foreach (var key in DesignDefaults.EntitiesWithoutCreatePath(manifest))
            errors.Add($"SMOKE: nothing in the app creates a '{key}' — every page that lists them is "
                     + "read-only, so the entity can never hold a record");

        // 4. A create form somebody can actually complete. Having a way in is not the same as being
        //    able to finish: an authored form is authoritative, so a required field it omits has no
        //    control anywhere and the dialog refuses to save while naming a field that is not on it.
        foreach (var missing in DesignDefaults.UnfillableRequiredFields(manifest))
            errors.Add($"SMOKE: '{missing}' is required but the create form does not offer it — the "
                     + "form cannot be completed, so no record can be created");

        // 5. Data a user can see. Only entities that are subjects of their own count: a config lookup
        //    is reached through the Configuration group, an owned child through its parent's detail,
        //    and a Forms submission or answer through its template.
        var shown = DesignDefaults.EntitiesShown(manifest);
        foreach (var e in (manifest["entities"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = Str(e, "key");
            if (key is null || shown.Contains(key) || !DesignDefaults.IsOwnSubject(e)) continue;
            errors.Add($"SMOKE: no page shows '{key}' — the entity exists but no screen renders it");
        }

        return errors;
    }

    private static string? Str(JsonNode? n, string key) =>
        (n as JsonObject)?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
