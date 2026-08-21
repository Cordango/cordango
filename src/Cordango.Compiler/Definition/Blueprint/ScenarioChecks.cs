// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition.Blueprints;

/// <summary>One scenario's verdict.</summary>
/// <param name="ScenarioId">Which scenario.</param>
/// <param name="Title">Its plain-language meaning, so a failure reads as a user problem.</param>
/// <param name="Passed">Whether every step and expectation resolved.</param>
/// <param name="Reasons">Why not. Empty on a pass.</param>
/// <param name="Notes">
/// Things that could NOT be checked, on a scenario that otherwise passed.
///
/// <para>Without this a partial check reads as a full one. A scenario whose only real assertion was
/// skipped would report green, and green is the word people act on — so an unverifiable expectation
/// has to leave a mark even when nothing failed.</para>
/// </param>
public sealed record ScenarioResult(
    string ScenarioId, string Title, bool Passed, IReadOnlyList<string> Reasons,
    IReadOnlyList<string>? Notes = null)
{
    /// <summary>Passed, and everything it claimed to check was actually checked.</summary>
    public bool FullyVerified => Passed && (Notes is null || Notes.Count == 0);
}

/// <summary>
/// <b>L1: static scenario checks.</b> Can the built app, structurally, support the tasks the user
/// said they need to do?
///
/// <para>This is the cheap tier and it is honest about being cheap. It proves the entity, the field,
/// the command and the surface each scenario names actually exist in the compiled definition — which
/// catches a whole class of "the app was generated but you cannot do the thing" without a database,
/// a tenant or a token.</para>
///
/// <para>What it deliberately does NOT prove: that a user has permission, that a required field can
/// be filled, that a guard passes, that the record is findable afterwards. Those need the app to
/// actually run, which is L2. Reporting an L1 pass as "verified" would be the same overclaim as
/// reporting a clean gate as a working app.</para>
/// </summary>
public static class ScenarioChecks
{
    public static IReadOnlyList<ScenarioResult> Static(Blueprint bp, JsonNode definition)
    {
        var doc = definition as JsonObject ?? [];
        return bp.Scenarios.Select(s => Check(bp, doc, s)).ToList();
    }

    private static ScenarioResult Check(Blueprint bp, JsonObject doc, ScenarioSpec scenario)
    {
        var reasons = new List<string>();

        if (bp.Actor(scenario.ActorId) is { } actor)
        {
            if (!Roles(doc).Contains(actor.Key))
                reasons.Add($"the built app has no '{actor.Name}' role to run this as");
        }
        else reasons.Add($"'{scenario.ActorId}' is not somebody this app knows about");

        foreach (var step in scenario.Given.Concat(scenario.Steps))
            CheckStep(bp, doc, step, reasons);

        foreach (var expectation in scenario.Expect)
            CheckExpectation(bp, doc, expectation, reasons);

        return new ScenarioResult(scenario.Id, scenario.Title, reasons.Count == 0, reasons);
    }

    private static void CheckStep(Blueprint bp, JsonObject doc, ScenarioStep step, List<string> reasons)
    {
        var entityKey = EntityKeyOf(bp, step.ConceptId);
        if (step.ConceptId is not null)
        {
            if (entityKey is null || Entity(doc, entityKey) is null)
            {
                reasons.Add($"there is nothing in the built app to {step.Op} — "
                    + $"'{ConceptName(bp, step.ConceptId)}' has no records");
                return;
            }
        }

        switch (step.Op)
        {
            case ScenarioOps.Create when entityKey is not null:
                foreach (var key in step.Values?.Keys ?? Enumerable.Empty<string>())
                    if (FieldKeyOf(bp, key) is not { } fieldKey || Field(doc, entityKey, fieldKey) is null)
                        reasons.Add($"a '{ConceptName(bp, step.ConceptId)}' cannot be created with "
                            + $"'{Label(bp, key)}' — the built app has no such field");
                break;

            case ScenarioOps.Command:
                if (CommandKeyOf(bp, step.ActionId) is not { } commandKey
                    || !Commands(doc, entityKey).Contains(commandKey))
                    reasons.Add($"the '{ActionLabel(bp, step.ActionId)}' action is not on the built app");
                break;

            case ScenarioOps.Transition:
                if (!TransitionExists(bp, doc, step.TransitionId, entityKey))
                    reasons.Add($"the built app cannot move a '{ConceptName(bp, step.ConceptId)}' "
                        + $"the way '{TransitionLabel(bp, step.TransitionId)}' describes");
                break;
        }
    }

    private static void CheckExpectation(Blueprint bp, JsonObject doc, ScenarioExpectation x,
        List<string> reasons)
    {
        switch (x.Op)
        {
            case ScenarioExpectOps.FieldEquals when x.FieldId is { } fid:
                if (FieldKeyOf(bp, fid) is not { } key || !AnyEntityHas(doc, key))
                    reasons.Add($"nothing in the built app records '{Label(bp, fid)}', so this cannot be checked");
                break;

            case ScenarioExpectOps.VisibleIn or ScenarioExpectOps.NotVisibleIn when x.SurfaceId is { } sid:
                var surface = bp.Experience?.Surface(sid);
                if (surface is null || !Views(doc).Contains(surface.Key))
                    reasons.Add($"the built app has no '{surface?.Label ?? sid}' screen to look at");
                break;
        }
    }

    // ---- resolution -------------------------------------------------------------------------

    private static string? EntityKeyOf(Blueprint bp, string? conceptId) =>
        conceptId is null ? null : bp.Concept(conceptId)?.Key;

    /// <summary>A scenario names a value by blueprint id; the definition knows it by key. Data
    /// fields, external references and relationship-backed references all resolve here.</summary>
    private static string? FieldKeyOf(Blueprint bp, string id) =>
        bp.Field(id)?.Key ?? bp.Reference(id)?.Key ?? bp.Relationship(id)?.Key;

    /// <summary>Mirrors lowering's rule: an action backing several transitions becomes one command
    /// per transition, so the command key is the transition's.</summary>
    private static string? CommandKeyOf(Blueprint bp, string? actionId)
    {
        if (actionId is null) return null;
        foreach (var w in bp.Workflows)
        {
            if (w.Actions.FirstOrDefault(a => a.Id == actionId) is not { } action) continue;
            var backed = w.Transitions.Where(t => t.ActionId == actionId).ToList();
            return backed.Count == 1 ? action.Key : backed.FirstOrDefault()?.Key ?? action.Key;
        }
        return null;
    }

    private static bool TransitionExists(Blueprint bp, JsonObject doc, string? transitionId, string? entityKey)
    {
        if (transitionId is null || entityKey is null) return false;
        foreach (var w in bp.Workflows)
        {
            if (w.Transitions.FirstOrDefault(t => t.Id == transitionId) is not { } transition) continue;
            var process = Arr(doc["processes"]).OfType<JsonObject>()
                .FirstOrDefault(p => Str(p, "entity") == entityKey);
            return process is not null && Arr(process["transitions"]).OfType<JsonObject>()
                .Any(t => Str(t, "key") == transition.Key);
        }
        return false;
    }

    // ---- definition readers -------------------------------------------------------------------

    private static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];
    private static string? Str(JsonNode? node, string key) => (node as JsonObject)?[key]?.GetValue<string>();

    private static JsonObject? Entity(JsonObject doc, string key) =>
        Arr(doc["entities"]).OfType<JsonObject>().FirstOrDefault(e => Str(e, "key") == key);

    private static JsonObject? Field(JsonObject doc, string entityKey, string fieldKey) =>
        Entity(doc, entityKey) is { } entity
            ? Arr(entity["fields"]).OfType<JsonObject>().FirstOrDefault(f => Str(f, "key") == fieldKey)
            : null;

    private static bool AnyEntityHas(JsonObject doc, string fieldKey) =>
        Arr(doc["entities"]).OfType<JsonObject>()
            .Any(e => Arr(e["fields"]).OfType<JsonObject>().Any(f => Str(f, "key") == fieldKey));

    private static HashSet<string> Commands(JsonObject doc, string? entityKey) =>
        Arr(doc["commands"]).OfType<JsonObject>()
            .Where(c => entityKey is null || Str(c, "entity") == entityKey)
            .Select(c => Str(c, "key")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Views(JsonObject doc) =>
        Arr(doc["views"]).OfType<JsonObject>().Select(v => Str(v, "key")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Roles(JsonObject doc) =>
        Arr(doc["roles"]).OfType<JsonObject>().Select(r => Str(r, "key")).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    // ---- labels, so a failure reads as a user problem -----------------------------------------

    private static string ConceptName(Blueprint bp, string? conceptId) =>
        conceptId is null ? "that record" : bp.Concept(conceptId)?.Name ?? conceptId;

    private static string Label(Blueprint bp, string id) =>
        bp.Field(id)?.Label ?? bp.Reference(id)?.Label ?? bp.Relationship(id)?.Label ?? id;

    private static string ActionLabel(Blueprint bp, string? actionId) =>
        actionId is null ? "that action"
            : bp.Workflows.SelectMany(w => w.Actions).FirstOrDefault(a => a.Id == actionId)?.Label ?? actionId;

    private static string TransitionLabel(Blueprint bp, string? transitionId) =>
        transitionId is null ? "that move"
            : bp.Workflows.SelectMany(w => w.Transitions).FirstOrDefault(t => t.Id == transitionId)?.Label
              ?? transitionId;
}
