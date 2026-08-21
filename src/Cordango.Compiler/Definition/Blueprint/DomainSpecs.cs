// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>What the app is called. The App Definition root requires a key, a name and a version;
/// the first two are the user's to choose, so they live in the blueprint rather than being invented
/// during lowering.</summary>
public sealed record AppSpec
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("handle")] public string? Handle { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
}

/// <summary>What the app fundamentally IS, named before anything is modelled. Shape-compatible with
/// the existing intent document, so <c>Prompts.FormatIntent</c> and <c>PromptModules.From</c> keep
/// working against a blueprint's intent without a translation layer.</summary>
public sealed record IntentSpec
{
    [JsonPropertyName("coreJob")] public string CoreJob { get; init; } = "";
    /// <summary>What success looks like for the USER, not for the app.</summary>
    [JsonPropertyName("primaryOutcome")] public string? PrimaryOutcome { get; init; }
    [JsonPropertyName("primaryUsers")] public IReadOnlyList<string> PrimaryUsers { get; init; } = [];
    [JsonPropertyName("primaryActor")] public string? PrimaryActor { get; init; }
    [JsonPropertyName("primaryEntity")] public string? PrimaryEntity { get; init; }
    [JsonPropertyName("workTopology")] public string WorkTopology { get; init; } = WorkTopologies.Record;
    [JsonPropertyName("behaviors")] public IReadOnlyList<string> Behaviors { get; init; } = [];
    [JsonPropertyName("primarySurface")] public PrimarySurface? PrimarySurface { get; init; }
    [JsonPropertyName("archetypeKind")] public string ArchetypeKind { get; init; } = ArchetypeKinds.Tracker;
    [JsonPropertyName("criticalDecisions")] public IReadOnlyList<string> CriticalDecisions { get; init; } = [];
    [JsonPropertyName("criticalQuantities")] public IReadOnlyList<string> CriticalQuantities { get; init; } = [];
    [JsonPropertyName("supportingConcepts")] public IReadOnlyList<string> SupportingConcepts { get; init; } = [];
    [JsonPropertyName("needsExtendedPersonData")] public bool? NeedsExtendedPersonData { get; init; }
    [JsonPropertyName("hasSubordinateRecords")] public bool? HasSubordinateRecords { get; init; }
    [JsonPropertyName("interpretation")] public string? Interpretation { get; init; }
    [JsonPropertyName("interpretationConfirmed")] public bool InterpretationConfirmed { get; init; }
    [JsonPropertyName("uncertaintyPlanningComplete")] public bool UncertaintyPlanningComplete { get; init; }
    [JsonPropertyName("approvedBrief")] public string? ApprovedBrief { get; init; }
    [JsonPropertyName("mainScreen")] public string? MainScreen { get; init; }
    [JsonPropertyName("mainActions")] public IReadOnlyList<string> MainActions { get; init; } = [];
}

public sealed record PrimarySurface
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>Who uses the app, what they are responsible for, and concretely what they may do.
/// Responsibilities alone do not lower into anything; permissions do. An actor list that captures
/// only prose produces an app where every role can do everything, which then makes multi-role
/// scenarios pass for the wrong reason.</summary>
public sealed record ActorSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("responsibilities")] public IReadOnlyList<string> Responsibilities { get; init; } = [];
    [JsonPropertyName("entityPermissions")] public IReadOnlyList<EntityPermission> EntityPermissions { get; init; } = [];
    /// <summary>Only the EXCEPTIONS. A field not listed follows its entity's permission.</summary>
    [JsonPropertyName("fieldPermissions")] public IReadOnlyList<FieldPermission> FieldPermissions { get; init; } = [];
    [JsonPropertyName("actionPermissions")] public IReadOnlyList<string> ActionPermissions { get; init; } = [];
}

public sealed record EntityPermission
{
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    [JsonPropertyName("create")] public bool Create { get; init; }
    [JsonPropertyName("read")] public bool Read { get; init; } = true;
    [JsonPropertyName("update")] public bool Update { get; init; }
    [JsonPropertyName("delete")] public bool Delete { get; init; }
}

public sealed record FieldPermission
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = "";
    [JsonPropertyName("visible")] public bool Visible { get; init; } = true;
    [JsonPropertyName("editable")] public bool Editable { get; init; } = true;
}

/// <summary>A thing the business works with, in the business's own word for it.</summary>
public sealed record ConceptSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("plural")] public string? Plural { get; init; }
    [JsonPropertyName("purpose")] public string? Purpose { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = ConceptKinds.Local;
    /// <summary>True for the concepts the app is ABOUT, false for supporting ones.</summary>
    [JsonPropertyName("central")] public bool Central { get; init; }
    [JsonPropertyName("sensitivity")] public string Sensitivity { get; init; } = Sensitivities.None;
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("recordLabel")] public RecordLabel? RecordLabel { get; init; }
    /// <summary><see cref="ConceptKinds.Enumeration"/> only.</summary>
    [JsonPropertyName("options")] public IReadOnlyList<OptionSpec> Options { get; init; } = [];
    /// <summary>Why this is an entity rather than a plain label set. Demanded by the gate when a
    /// concept looks like an enumeration, so the entity-vs-enumeration call is argued rather than
    /// guessed from the concept's name.</summary>
    [JsonPropertyName("entityJustification")] public string? EntityJustification { get; init; }
}

/// <summary>How one record is named on screen. ONE field: the App Definition's <c>displayField</c>
/// takes a field key and nothing else, there is no <c>displayTemplate</c>, and computed fields are
/// derived rather than durable human labels — so a composite label cannot be materialised either. A concept that genuinely needs
/// one records it as <see cref="MissingCapabilities.CompositeRecordLabel"/> rather than faking it.</summary>
public sealed record RecordLabel
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = "";
    [JsonPropertyName("example")] public string? Example { get; init; }
}

public sealed record OptionSpec
{
    [JsonPropertyName("value")] public string Value { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("phase")] public string? Phase { get; init; }
}

/// <summary>A dataset this app REFERENCES but does not own: the platform directory, or a core app
/// addressed by its permanent <c>SystemKey</c>. Declared once however many fields point at it —
/// requester, assignee, approver and reviewer are four <see cref="ReferenceFieldSpec"/> against one
/// of these, not four concepts.</summary>
public sealed record ExternalConceptSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = ExternalSources.Platform;
    [JsonPropertyName("entityKey")] public string EntityKey { get; init; } = "";
    /// <summary><see cref="ExternalSources.Core"/> only: the core app's permanent SystemKey. Never a
    /// handle — handles are disposable addresses that collide and get suffixed.</summary>
    [JsonPropertyName("coreSystemKey")] public string? CoreSystemKey { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

/// <summary>A relationship between two LOCAL concepts, carrying enough to lower deterministically:
/// which side stores the key, what happens on delete, and whether the child only exists inside the
/// parent. A bare (to, kind, label) triple cannot produce a valid <c>relation</c> — the App
/// Definition needs key, label, type, fromEntity, toEntity and sometimes inverseField.</summary>
public sealed record RelationshipSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("fromConceptId")] public string FromConceptId { get; init; } = "";
    [JsonPropertyName("toConceptId")] public string ToConceptId { get; init; } = "";
    [JsonPropertyName("cardinality")] public string Cardinality { get; init; } = Cardinalities.OneToMany;
    /// <summary>Which side holds the reference. <c>to</c> means the toConcept carries a reference
    /// pointing back at the fromConcept — the normal shape for one-to-many (many quote lines point
    /// at one quote).</summary>
    [JsonPropertyName("storageOwner")] public string StorageOwner { get; init; } = StorageOwners.To;
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("inverseLabel")] public string? InverseLabel { get; init; }
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("deleteBehavior")] public string DeleteBehavior { get; init; } = DeleteBehaviors.Restrict;
    /// <summary>The child cannot exist without the parent and is never browsed on its own. Lowers to
    /// <c>ownedBy</c>.</summary>
    [JsonPropertyName("isComposition")] public bool IsComposition { get; init; }
    [JsonPropertyName("childPresentation")] public string? ChildPresentation { get; init; }
    [JsonPropertyName("purpose")] public string? Purpose { get; init; }

    /// <summary>The concept that ends up carrying the reference field. Derived, so not serialised.</summary>
    [JsonIgnore] public string OwningConceptId => StorageOwner == StorageOwners.From ? FromConceptId : ToConceptId;

    /// <summary>The concept the reference field points AT. Derived, so not serialised.</summary>
    [JsonIgnore] public string TargetConceptId => StorageOwner == StorageOwners.From ? ToConceptId : FromConceptId;
}

/// <summary>One local field pointing at an external dataset, with the ROLE it plays. The semantic
/// role is what distinguishes four person references on the same record from each other.</summary>
public sealed record ReferenceFieldSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("onConceptId")] public string OnConceptId { get; init; } = "";
    [JsonPropertyName("externalConceptId")] public string ExternalConceptId { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("semanticRole")] public string SemanticRole { get; init; } = "";
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("deleteBehavior")] public string DeleteBehavior { get; init; } = DeleteBehaviors.Restrict;
}

/// <summary>The information the app collects, approved before anything is generated. Reference
/// fields are deliberately absent: they come from <see cref="RelationshipSpec"/> and <see
/// cref="ReferenceFieldSpec"/>, so every field has exactly one source of truth.</summary>
public sealed record DataSpec
{
    [JsonPropertyName("fields")] public IReadOnlyList<FieldSpec> Fields { get; init; } = [];
}

public sealed record FieldSpec
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = FieldTypes.Text;
    /// <summary>Why the USER needs this field. Grouping by purpose is how the wizard shows fields
    /// without asking anyone to design a database.</summary>
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = FieldPurposes.Context;
    /// <summary>Where this field came from. A fact about origin, not a model-supplied confidence
    /// score — only <see cref="FieldProvenance.ModelSuggested"/> fields are offered for opt-in.</summary>
    [JsonPropertyName("provenance")] public string Provenance { get; init; } = FieldProvenance.ModelSuggested;
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("unique")] public bool Unique { get; init; }
    [JsonPropertyName("indexed")] public bool Indexed { get; init; }
    [JsonPropertyName("help")] public string? Help { get; init; }
    [JsonPropertyName("group")] public string? Group { get; init; }
    [JsonPropertyName("default")] public System.Text.Json.JsonElement? Default { get; init; }
    [JsonPropertyName("unit")] public string? Unit { get; init; }
    [JsonPropertyName("currency")] public string? Currency { get; init; }
    [JsonPropertyName("precision")] public int? Precision { get; init; }
    [JsonPropertyName("scale")] public int? Scale { get; init; }
    /// <summary>select/multiselect: take options from an <see cref="ConceptKinds.Enumeration"/>
    /// concept instead of listing them inline.</summary>
    [JsonPropertyName("optionsFromConceptId")] public string? OptionsFromConceptId { get; init; }
    [JsonPropertyName("options")] public IReadOnlyList<OptionSpec> Options { get; init; } = [];
    [JsonPropertyName("computed")] public ComputedSpec? Computed { get; init; }
}

/// <summary>Server-calculated and never typed. Exactly one of <see cref="Expr"/> or <see
/// cref="Rollup"/> — enforced by <see cref="BlueprintGate"/> rather than a schema <c>oneOf</c>,
/// which crashes the JsonSchema.Net host.</summary>
public sealed record ComputedSpec
{
    /// <summary>Typed expression over this record, written with FIELD IDS. Arithmetic returns a
    /// number; comparisons and boolean logic return a boolean. Expressions may read other computed
    /// fields when their dependency graph is acyclic. Lowering substitutes keys, so renaming a field
    /// never breaks a formula.</summary>
    [JsonPropertyName("expr")] public string? Expr { get; init; }
    [JsonPropertyName("rollup")] public RollupSpec? Rollup { get; init; }
}

public sealed record RollupSpec
{
    [JsonPropertyName("conceptId")] public string ConceptId { get; init; } = "";
    [JsonPropertyName("viaRelationshipId")] public string ViaRelationshipId { get; init; } = "";
    [JsonPropertyName("op")] public string Op { get; init; } = AggregateOps.Count;
    [JsonPropertyName("fieldId")] public string? FieldId { get; init; }
    [JsonPropertyName("filters")] public IReadOnlyList<Filter> Filters { get; init; } = [];
}
