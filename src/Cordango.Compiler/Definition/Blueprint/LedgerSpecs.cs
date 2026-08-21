// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>An executable acceptance test in the user's terms. Typed and closed on purpose:
/// natural-language steps would need another model to run them, which is what this architecture
/// exists to stop doing. The user approves <see cref="Title"/>; the steps are the machine's
/// business.</summary>
public sealed record ScenarioSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("actorId")] public string ActorId { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("verifiesRequirementIds")] public IReadOnlyList<string> VerifiesRequirementIds { get; init; } = [];
    /// <summary>True when this proves something is REFUSED. A requirement with <see
    /// cref="Requirement.ImpliesProhibition"/> needs one, or the prohibition is untested and the app
    /// can pass every scenario while allowing exactly what the user said must not happen.</summary>
    [JsonPropertyName("negative")] public bool Negative { get; init; }
    [JsonPropertyName("given")] public IReadOnlyList<ScenarioStep> Given { get; init; } = [];
    [JsonPropertyName("steps")] public IReadOnlyList<ScenarioStep> Steps { get; init; } = [];
    [JsonPropertyName("expect")] public IReadOnlyList<ScenarioExpectation> Expect { get; init; } = [];
}

public sealed record ScenarioStep
{
    [JsonPropertyName("op")] public string Op { get; init; } = ScenarioOps.Create;
    /// <summary>Overrides the scenario's default actor for this step. Multi-actor journeys such as
    /// employee submits then manager approves cannot be tested honestly with one actor throughout.</summary>
    [JsonPropertyName("asActorId")] public string? AsActorId { get; init; }
    [JsonPropertyName("conceptId")] public string? ConceptId { get; init; }
    /// <summary>Binds the resulting record to a name later steps refer to.</summary>
    [JsonPropertyName("as")] public string? As { get; init; }
    [JsonPropertyName("record")] public ScenarioRef? Record { get; init; }
    [JsonPropertyName("actionId")] public string? ActionId { get; init; }
    [JsonPropertyName("transitionId")] public string? TransitionId { get; init; }
    /// <summary>Field id to value. A value may be a literal or <c>{ "ref": "&lt;binding&gt;" }</c>.</summary>
    [JsonPropertyName("values")] public Dictionary<string, System.Text.Json.JsonElement>? Values { get; init; }
    /// <summary>The step itself must be REJECTED. This is how a prohibition is tested.</summary>
    [JsonPropertyName("expectRefused")] public bool ExpectRefused { get; init; }
}

public sealed record ScenarioRef
{
    [JsonPropertyName("ref")] public string Ref { get; init; } = "";
}

public sealed record ScenarioExpectation
{
    [JsonPropertyName("op")] public string Op { get; init; } = ScenarioExpectOps.FieldEquals;
    [JsonPropertyName("record")] public ScenarioRef? Record { get; init; }
    [JsonPropertyName("fieldId")] public string? FieldId { get; init; }
    [JsonPropertyName("value")] public System.Text.Json.JsonElement? Value { get; init; }
    [JsonPropertyName("surfaceId")] public string? SurfaceId { get; init; }
    [JsonPropertyName("conceptId")] public string? ConceptId { get; init; }
    [JsonPropertyName("count")] public int? Count { get; init; }
    [JsonPropertyName("asActorId")] public string? AsActorId { get; init; }
}

/// <summary>One thing the app must do. <see cref="DesignCoverage"/> and <see cref="Verification"/>
/// are COMPUTED — a model never marks its own work covered, and they are separate fields because
/// "the structure exists" and "a user could actually do it" are different claims that fail
/// independently.</summary>
public sealed record Requirement
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("statement")] public string Statement { get; init; } = "";
    [JsonPropertyName("importance")] public string Importance { get; init; } = Importances.Important;
    [JsonPropertyName("expectedOutcome")] public string? ExpectedOutcome { get; init; }
    [JsonPropertyName("impliesProhibition")] public bool ImpliesProhibition { get; init; }
    [JsonPropertyName("origin")] public RequirementOrigin? Origin { get; init; }
    /// <summary>Blueprint element ids that satisfy this. Each must resolve, or coverage is partial.</summary>
    [JsonPropertyName("coveredBy")] public IReadOnlyList<string> CoveredBy { get; init; } = [];
    [JsonPropertyName("verifiedBy")] public IReadOnlyList<string> VerifiedBy { get; init; } = [];
    /// <summary>COMPUTED at lowering. Absent until then.</summary>
    [JsonPropertyName("designCoverage")] public string? DesignCoverage { get; init; }
    /// <summary>COMPUTED after scenarios run. Absent until then.</summary>
    [JsonPropertyName("verification")] public string? Verification { get; init; }
}

public sealed record RequirementOrigin
{
    [JsonPropertyName("type")] public string Type { get; init; } = RequirementOrigins.Brief;
    [JsonPropertyName("uncertaintyId")] public string? UncertaintyId { get; init; }
}

/// <summary>An unresolved decision the interview may ask about. Consumed by the question planner —
/// an uncertainty list nothing reads is decoration, and was the flaw in the first design.</summary>
public sealed record Uncertainty
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("question")] public string Question { get; init; } = "";
    [JsonPropertyName("whyItMatters")] public string WhyItMatters { get; init; } = "";
    [JsonPropertyName("affectsLayer")] public string AffectsLayer { get; init; } = SpecLayers.Concepts;
    [JsonPropertyName("suggestedAnswers")] public IReadOnlyList<string> SuggestedAnswers { get; init; } = [];
    /// <summary>True when proceeding without an answer produces the WRONG app rather than a
    /// defaulted one. Only these block approval; the rest become recorded assumptions.</summary>
    [JsonPropertyName("blocksLowering")] public bool BlocksLowering { get; init; }
    [JsonPropertyName("resolution")] public UncertaintyResolution? Resolution { get; init; }
}

public sealed record UncertaintyResolution
{
    [JsonPropertyName("source")] public string Source { get; init; } = ResolutionSources.UserAnswer;
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("revision")] public int? Revision { get; init; }
}

public sealed record Assumption
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("statement")] public string Statement { get; init; } = "";
    [JsonPropertyName("layer")] public string Layer { get; init; } = SpecLayers.Concepts;
    [JsonPropertyName("revision")] public int? Revision { get; init; }
}

/// <summary>Something the user asked for that the platform cannot do, recorded honestly rather than
/// silently dropped or faked into a screen that does not work.</summary>
public sealed record UnsupportedItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("requirementId")] public string? RequirementId { get; init; }
    /// <summary>The concept this limitation applies to, when it is about one. A composite record
    /// label is declared this way: the concept has no <c>recordLabel</c> BECAUSE the platform cannot
    /// express one, which is a recorded gap rather than an oversight.</summary>
    [JsonPropertyName("conceptId")] public string? ConceptId { get; init; }
    /// <summary>The missing platform capability, named from a closed set so the same gap reads the
    /// same way across every blueprint and can be counted.</summary>
    [JsonPropertyName("capability")] public string Capability { get; init; } = MissingCapabilities.Other;
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    /// <summary>True when a CRITICAL requirement depends on it. Publication is refused rather than
    /// shipping an app that quietly does not do the thing it was asked for.</summary>
    [JsonPropertyName("blocksPublication")] public bool BlocksPublication { get; init; }
}
