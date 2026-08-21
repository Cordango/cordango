// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>Durable proof that a proposer pass completed, even when its valid output was empty.</summary>
public sealed record GenerationCheckpoint
{
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("scopeId")] public string? ScopeId { get; init; }
    [JsonPropertyName("promptVersion")] public string PromptVersion { get; init; } = "";
    [JsonPropertyName("completedRevision")] public int CompletedRevision { get; init; }
}

public sealed record BlueprintNextAction(
    string Kind,
    string? ReviewKey = null,
    string? Role = null,
    string? ScopeId = null,
    string? Label = null);

/// <summary>Deterministically decides the next step. The browser renders this decision rather than
/// inferring progress from arrays whose valid result may be empty.</summary>
public static class GenerationFlow
{
    /// <summary>Step 1: interpret the spark into a Brief. Two model roles, one user-facing step.</summary>
    public const string BootstrapRole = "bootstrap";

    /// <summary>Step 2: one agent session writes the whole App Definition. Named here, in the layer
    /// that decides the flow, so the job type and the flow cannot disagree about what it is called.</summary>
    public const string DesignRole = "design";

    public static BlueprintNextAction Next(Blueprint blueprint, bool generationRunning = false)
    {
        var bp = ReviewFlow.Reconcile(blueprint);
        if (generationRunning) return new("wait", Label: "Generation is running");

        bool Done(string role) => bp.Generation.Any(x => x.Role == role);
        ReviewState? Review(string key) => bp.Reviews.FirstOrDefault(x => x.Key == key);
        bool Approved(string key) => Review(key)?.Status == ReviewStatuses.Approved;
        BlueprintNextAction Generate(string role, string label, string? scope = null) =>
            new("generate", Role: role, ScopeId: scope, Label: label);
        BlueprintNextAction ReviewAction(string key) => new("review", ReviewKey: key,
            ScopeId: Review(key)?.ScopeId, Label: "Review this layer");

        // ---- THREE STEPS ---------------------------------------------------------------------
        //
        // 1. UNDERSTAND — one call turns the spark into a Brief: a name, a purpose, open questions.
        // 2. DESIGN     — one agent session writes the whole App Definition, validating as it goes.
        // 3. REVIEW     — look at the app that exists, and publish it or ask for changes.
        //
        // What used to be here: eight more proposer passes (concepts, relationships, data, workflows,
        // actors, jobs, experience, scenarios) and five more approval gates between them. Each pass
        // saw only its own slice, so ids had to agree across calls that could not see each other —
        // six of the 2026-08-04 run's errors came from that single cause. And a person was asked to
        // approve five near-identical screens describing an app that did not exist yet.
        //
        // The steps below are ORDERED, not optional: nothing here decides what the app should be, it
        // decides which of three things happens next.

        if (!Done("interpreter") || !Done("uncertainty_planner"))
            return Generate(BootstrapRole, "Interpret the description");

        // ONE review of the Brief. Identity and intent were two screens rendering the same card.
        if (!Approved(ReviewLayers.Identity)) return ReviewAction(ReviewLayers.Identity);

        // Open questions are part of understanding, and only the ones that would produce the WRONG
        // app block progress — the rest become recorded assumptions the design session is told to
        // decide for itself.
        if (NextQuestion.Next(bp) is { BlocksLowering: true }) return new("answer", Label: "Answer the next question");

        if (!Done(DesignRole)) return Generate(DesignRole, "Design the app");

        // The app exists by now, so the last decision is made against the app itself rather than
        // against a description of one.
        return new BlueprintNextAction("review-app", ReviewKey: ReviewLayers.App,
            Label: "Review the app");
    }
}
