// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// A user-approved product specification: what the user agreed the app should be, in their own
/// vocabulary, with stable identity. Deliberately NOT an App Definition — <see
/// cref="BlueprintLowering"/> turns an approved blueprint into one deterministically, with no model
/// call.
///
/// <para>Every element carries an immutable id independent of its key. Keys are renameable labels;
/// ids never change and are never reused. That is what lets a later revision say "this concept was
/// renamed" rather than "one concept vanished and another appeared", which is the difference between
/// a migration and data loss. It cannot be retrofitted, so it is here from the first revision.</para>
///
/// <para>Values that look like enums are plain strings validated by the schema and <see
/// cref="BlueprintGate"/>, with their legal values in the <c>*Kinds</c> constant classes — the same
/// posture as <c>JobStatus</c> in the control plane. It keeps round-tripping byte-stable without
/// serializer naming policies, which matters because an approved revision is content-hashed.</para>
/// </summary>
public sealed record Blueprint
{
    /// <summary>The blueprint schema version this document was authored against. Stamped on every
    /// revision so an older blueprint is upgraded rather than misread.</summary>
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = BlueprintSchemaVersion.Current;
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("revision")] public int Revision { get; init; } = 1;
    [JsonPropertyName("parentRevision")] public int? ParentRevision { get; init; }

    /// <summary>Content hash of this document with the field itself removed. Stamped on approval;
    /// an approved revision is immutable and this is what proves it.</summary>
    [JsonPropertyName("contentHash")] public string? ContentHash { get; init; }

    /// <summary>The user's own sentence, verbatim, forever. The only part of the blueprint that
    /// nobody else wrote.</summary>
    [JsonPropertyName("spark")] public string Spark { get; init; } = "";

    /// <summary>What the app is called. Lowering needs a key and a name for the definition root, and
    /// inventing them at lowering time would mean the user never approved the name of the thing they
    /// approved.</summary>
    [JsonPropertyName("app")] public AppSpec App { get; init; } = new();

    [JsonPropertyName("intent")] public IntentSpec Intent { get; init; } = new();
    [JsonPropertyName("actors")] public IReadOnlyList<ActorSpec> Actors { get; init; } = [];
    [JsonPropertyName("concepts")] public IReadOnlyList<ConceptSpec> Concepts { get; init; } = [];
    [JsonPropertyName("externals")] public IReadOnlyList<ExternalConceptSpec> Externals { get; init; } = [];
    [JsonPropertyName("relationships")] public IReadOnlyList<RelationshipSpec> Relationships { get; init; } = [];
    [JsonPropertyName("references")] public IReadOnlyList<ReferenceFieldSpec> References { get; init; } = [];
    [JsonPropertyName("data")] public DataSpec? Data { get; init; }
    [JsonPropertyName("workflows")] public IReadOnlyList<WorkflowSpec> Workflows { get; init; } = [];
    [JsonPropertyName("jobs")] public IReadOnlyList<JobSpec> Jobs { get; init; } = [];
    [JsonPropertyName("experience")] public ExperienceSpec? Experience { get; init; }
    [JsonPropertyName("scenarios")] public IReadOnlyList<ScenarioSpec> Scenarios { get; init; } = [];
    [JsonPropertyName("ledger")] public IReadOnlyList<Requirement> Ledger { get; init; } = [];
    [JsonPropertyName("uncertainties")] public IReadOnlyList<Uncertainty> Uncertainties { get; init; } = [];
    [JsonPropertyName("assumptions")] public IReadOnlyList<Assumption> Assumptions { get; init; } = [];
    [JsonPropertyName("unsupported")] public IReadOnlyList<UnsupportedItem> Unsupported { get; init; } = [];
    [JsonPropertyName("reviews")] public IReadOnlyList<ReviewState> Reviews { get; init; } = [];
    [JsonPropertyName("changeRequests")] public IReadOnlyList<BlueprintChangeRequest> ChangeRequests { get; init; } = [];
    [JsonPropertyName("generation")] public IReadOnlyList<GenerationCheckpoint> Generation { get; init; } = [];

    /// <summary>Every field declared anywhere in the blueprint. Reference fields live in <see
    /// cref="Relationships"/> and <see cref="References"/> rather than in <see cref="DataSpec"/>, so
    /// this is the only honest way to ask "does this field id exist".
    /// <para>Ignored when serialising: the schema is <c>additionalProperties: false</c>, and a
    /// derived convenience property is not part of the document.</para></summary>
    [JsonIgnore] public IEnumerable<FieldSpec> AllFields => Data?.Fields ?? [];

    public ConceptSpec? Concept(string? id) => id is null ? null : Concepts.FirstOrDefault(c => c.Id == id);
    public FieldSpec? Field(string? id) => id is null ? null : AllFields.FirstOrDefault(f => f.Id == id);
    public ActorSpec? Actor(string? id) => id is null ? null : Actors.FirstOrDefault(a => a.Id == id);
    public RelationshipSpec? Relationship(string? id) => id is null ? null : Relationships.FirstOrDefault(r => r.Id == id);
    public ReferenceFieldSpec? Reference(string? id) => id is null ? null : References.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Resolves a column/filter/sort/value target. Three things can name a value on a record: a data
    /// field (<c>fld_</c>), an external reference (<c>ref_</c>), and a local relationship
    /// (<c>rel_</c>) — because the relationship IS the reference field on whichever side stores it.
    /// A quote list showing its customer names the relationship, not a field that does not
    /// separately exist.
    /// </summary>
    public (bool Found, string? Type) ResolveFieldTarget(string? id)
    {
        if (id is null) return (false, null);
        if (Field(id) is { } f) return (true, f.Type);
        if (Reference(id) is not null) return (true, FieldTypes.Reference);
        if (Relationship(id) is not null) return (true, FieldTypes.Reference);
        return (false, null);
    }

    /// <summary>Which concept a value target sits ON, for the "this belongs to another concept"
    /// check. Null when the id resolves to nothing.</summary>
    public string? OwnerOfFieldTarget(string? id) =>
        Field(id)?.ConceptId ?? Reference(id)?.OnConceptId ?? Relationship(id)?.OwningConceptId;
}

/// <summary>The blueprint schema version. Bumping it requires an upgrade step in
/// <c>BlueprintUpgrade</c> and a fixture proving an older document still loads — settled before any
/// user blueprint persists, not after.</summary>
public static class BlueprintSchemaVersion
{
    public const string Current = "1.1";
}

/// <summary>Parse/serialise. One shared options instance so a document written twice is
/// byte-identical: an approved revision is content-hashed, and a hash that moves because the
/// serializer was configured differently would make immutability a lie.</summary>
public static class BlueprintJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Property order follows declaration order, which is stable across runs and readable in a diff.
        PropertyNamingPolicy = null,
    };

    public static Blueprint Parse(string json) =>
        JsonSerializer.Deserialize<Blueprint>(json, Options)
        ?? throw new InvalidOperationException("blueprint document parsed to null");

    public static Blueprint? TryParse(string json)
    {
        try { return JsonSerializer.Deserialize<Blueprint>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static string ToJson(Blueprint blueprint) => JsonSerializer.Serialize(blueprint, Options);

    /// <summary>The document as a <see cref="JsonNode"/>, for structural validation against the
    /// embedded blueprint schema.</summary>
    public static JsonNode ToNode(Blueprint blueprint) => JsonNode.Parse(ToJson(blueprint))!;

    /// <summary>Content hash over the document with <c>contentHash</c> itself removed. Stamped at
    /// approval and re-checkable afterwards to prove an approved revision was not edited.</summary>
    public static string ContentHash(Blueprint blueprint)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(ToJson(blueprint with { ContentHash = null }));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
