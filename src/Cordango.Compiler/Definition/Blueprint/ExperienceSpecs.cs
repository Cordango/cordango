// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>The lifecycle of one concept: the states records move through, the legal moves between
/// them, and the actions users invoke.</summary>
public sealed record WorkflowSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    /// <summary>The key of the status field this workflow governs. Lowering creates the field and
    /// the process together, and the field never carries its own options — the App Definition
    /// requires the governing process to own them, and authoring both is an error.</summary>
    [JsonPropertyName("statusFieldKey")] public string StatusFieldKey { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("states")] public IReadOnlyList<StateSpec> States { get; init; } = [];
    [JsonPropertyName("transitions")] public IReadOnlyList<TransitionSpec> Transitions { get; init; } = [];
    [JsonPropertyName("actions")] public IReadOnlyList<ActionSpec> Actions { get; init; } = [];

    public StateSpec? State(string? id) => id is null ? null : States.FirstOrDefault(s => s.Id == id);
}

public sealed record StateSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("phase")] public string Phase { get; init; } = Phases.Active;
    [JsonPropertyName("initial")] public bool Initial { get; init; }
    [JsonPropertyName("terminal")] public bool Terminal { get; init; }
    [JsonPropertyName("color")] public string? Color { get; init; }
}

public sealed record TransitionSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("fromStateId")] public string FromStateId { get; init; } = "";
    [JsonPropertyName("toStateId")] public string ToStateId { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("actionId")] public string? ActionId { get; init; }
    /// <summary>Fields that MUST have a value for this move to be legal — a rejection reason, a loss
    /// reason. This is how "must give a reason" stops being a convention people work around.</summary>
    [JsonPropertyName("requiresFieldIds")] public IReadOnlyList<string> RequiresFieldIds { get; init; } = [];
    [JsonPropertyName("guard")] public Filter? Guard { get; init; }
    [JsonPropertyName("guardDescription")] public string? GuardDescription { get; init; }
}

public sealed record ActionSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("appearsOn")] public string AppearsOn { get; init; } = ActionPlacements.Detail;
    [JsonPropertyName("confirm")] public string? Confirm { get; init; }
    /// <summary>Fields COLLECTED when the action runs, rather than filled in on the ordinary form.</summary>
    [JsonPropertyName("inputFieldIds")] public IReadOnlyList<string> InputFieldIds { get; init; } = [];
}

/// <summary>A job to be done: one actor, one recurring task, and the surface that serves it. The
/// user's own words live here; the <see cref="SurfaceSpec"/> it produces is what lowers. Keeping
/// them apart is what stops a natural-language filter like "assigned to me and active" from ever
/// reaching the compiler, where it could not be resolved without another model.</summary>
public sealed record JobSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("actorId")] public string ActorId { get; init; } = "";
    [JsonPropertyName("task")] public string Task { get; init; } = "";
    [JsonPropertyName("frequency")] public string? Frequency { get; init; }
    [JsonPropertyName("primaryConceptId")] public string PrimaryConceptId { get; init; } = "";
    [JsonPropertyName("servedBySurfaceId")] public string? ServedBySurfaceId { get; init; }
    /// <summary>Interview evidence, not a lowering input.</summary>
    [JsonPropertyName("importantInformation")] public IReadOnlyList<string> ImportantInformation { get; init; } = [];
}

/// <summary>The approved screen roster: semantic UI intent plus deterministic layout conventions.
/// Enough that lowering never guesses, not so much that the wizard becomes a definition editor —
/// block trees, spacing and component choice are lowering policy, not blueprint content.</summary>
public sealed record ExperienceSpec
{
    [JsonPropertyName("surfaces")] public IReadOnlyList<SurfaceSpec> Surfaces { get; init; } = [];
    [JsonPropertyName("pages")] public IReadOnlyList<PageSpec> Pages { get; init; } = [];
    /// <summary>Which page each actor opens on. Different actors landing in different places is most
    /// of what makes a multi-role app feel built for its users rather than at them.</summary>
    [JsonPropertyName("landing")] public IReadOnlyList<LandingSpec> Landing { get; init; } = [];

    public SurfaceSpec? Surface(string? id) => id is null ? null : Surfaces.FirstOrDefault(s => s.Id == id);
    public PageSpec? Page(string? id) => id is null ? null : Pages.FirstOrDefault(p => p.Id == id);
}

public sealed record SurfaceSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("surface")] public string Surface { get; init; } = SurfaceKinds.Table;
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    /// <summary><c>global</c> = one surface over the collection. <c>per_record</c> = one per record
    /// of <see cref="ScopeConceptId"/>, composed inside that record's detail. The cardinality call:
    /// "one matrix" where the domain calls for "one matrix per employee" is the failure this
    /// prevents.</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = SurfaceScopes.Global;
    [JsonPropertyName("scopeConceptId")] public string? ScopeConceptId { get; init; }
    /// <summary>board: the field whose values become columns.</summary>
    [JsonPropertyName("groupByFieldId")] public string? GroupByFieldId { get; init; }
    [JsonPropertyName("dateStartFieldId")] public string? DateStartFieldId { get; init; }
    [JsonPropertyName("dateEndFieldId")] public string? DateEndFieldId { get; init; }
    [JsonPropertyName("columns")] public IReadOnlyList<string> Columns { get; init; } = [];
    [JsonPropertyName("filters")] public IReadOnlyList<Filter> Filters { get; init; } = [];
    [JsonPropertyName("sort")] public IReadOnlyList<SortSpec> Sort { get; init; } = [];
    [JsonPropertyName("sortableFieldIds")] public IReadOnlyList<string> SortableFieldIds { get; init; } = [];
    [JsonPropertyName("filterableFieldIds")] public IReadOnlyList<string> FilterableFieldIds { get; init; } = [];
    [JsonPropertyName("metrics")] public IReadOnlyList<MetricSpec> Metrics { get; init; } = [];
    /// <summary>detail: the child or related concepts shown alongside the record.</summary>
    [JsonPropertyName("relatedConceptIds")] public IReadOnlyList<string> RelatedConceptIds { get; init; } = [];
}

public sealed record SortSpec
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = "";
    [JsonPropertyName("direction")] public string Direction { get; init; } = "asc";
}

public sealed record MetricSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    [JsonPropertyName("aggregate")] public string Aggregate { get; init; } = AggregateOps.Count;
    [JsonPropertyName("fieldId")] public string? FieldId { get; init; }
    [JsonPropertyName("groupByFieldId")] public string? GroupByFieldId { get; init; }
    [JsonPropertyName("filters")] public IReadOnlyList<Filter> Filters { get; init; } = [];
}

public sealed record PageSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = PageRoles.Workspace;
    [JsonPropertyName("surfaceIds")] public IReadOnlyList<string> SurfaceIds { get; init; } = [];
    [JsonPropertyName("brief")] public string? Brief { get; init; }
}

public sealed record LandingSpec
{
    [JsonPropertyName("actorId")] public string ActorId { get; init; } = "";
    [JsonPropertyName("pageId")] public string PageId { get; init; } = "";
}

/// <summary>Narrow page composition patch: which approved surfaces appear on one page.</summary>
public sealed record PageCompositionSpec
{
    [JsonPropertyName("pageId")] public string PageId { get; init; } = "";
    [JsonPropertyName("surfaceIds")] public IReadOnlyList<string> SurfaceIds { get; init; } = [];
}

/// <summary>A leaf condition. Exactly one of <see cref="FilterValue.Literal"/> or <see
/// cref="FilterValue.Context"/> must be present — enforced by <see cref="BlueprintGate"/> rather
/// than a schema <c>oneOf</c>.</summary>
public sealed record Filter
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = "";
    [JsonPropertyName("operator")] public string Operator { get; init; } = FilterOperators.Eq;
    [JsonPropertyName("value")] public FilterValue? Value { get; init; }
}

public sealed record FilterValue
{
    [JsonPropertyName("literal")] public System.Text.Json.JsonElement? Literal { get; init; }
    /// <summary>Resolved at read time from who is asking, or when. This is how "assigned to me"
    /// becomes something a compiler can emit.</summary>
    [JsonPropertyName("context")] public string? Context { get; init; }
}
