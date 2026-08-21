// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>Where one blueprint element ended up in the lowered definition.</summary>
/// <param name="BlueprintId">The immutable blueprint id.</param>
/// <param name="EntityKey">The entity it lives on, for anything entity-scoped. Null at app level.</param>
/// <param name="Key">The definition key it lowered to. Renameable — the pair is the point.</param>
public sealed record IdentityEntry(string BlueprintId, string? EntityKey, string Key);

/// <summary>
/// The correspondence between immutable blueprint ids and the renameable keys they lowered to.
///
/// <para>This is what turns "one concept vanished and another appeared" into "this concept was
/// renamed", which is the difference between a migration and data loss. It cannot be reconstructed
/// after the fact from two definitions, because a rename and a delete-plus-add look identical there;
/// only the id says which happened.</para>
///
/// <para>It deliberately stops at definition keys. Physical table and column names do not exist
/// until schema provisioning, so mapping to them is a separate <c>DeploymentIdentityMap</c> produced
/// during runtime build and migration — putting them here would be a claim lowering is in no
/// position to make.</para>
/// </summary>
public sealed record DefinitionIdentityMap
{
    [JsonPropertyName("entities")] public IReadOnlyList<IdentityEntry> Entities { get; init; } = [];
    /// <summary>Data fields, external references and relationship-backed references alike — every
    /// value that ended up as a column on a record.</summary>
    [JsonPropertyName("fields")] public IReadOnlyList<IdentityEntry> Fields { get; init; } = [];
    [JsonPropertyName("relations")] public IReadOnlyList<IdentityEntry> Relations { get; init; } = [];
    [JsonPropertyName("processes")] public IReadOnlyList<IdentityEntry> Processes { get; init; } = [];
    [JsonPropertyName("states")] public IReadOnlyList<IdentityEntry> States { get; init; } = [];
    [JsonPropertyName("transitions")] public IReadOnlyList<IdentityEntry> Transitions { get; init; } = [];
    [JsonPropertyName("commands")] public IReadOnlyList<IdentityEntry> Commands { get; init; } = [];
    [JsonPropertyName("views")] public IReadOnlyList<IdentityEntry> Views { get; init; } = [];
    [JsonPropertyName("pages")] public IReadOnlyList<IdentityEntry> Pages { get; init; } = [];
    [JsonPropertyName("roles")] public IReadOnlyList<IdentityEntry> Roles { get; init; } = [];

    /// <summary>Every entry, for the "does this blueprint id appear anywhere in the lowered
    /// definition" question the requirement ledger asks.</summary>
    [JsonIgnore]
    public IEnumerable<IdentityEntry> All =>
        Entities.Concat(Fields).Concat(Relations).Concat(Processes).Concat(States)
            .Concat(Transitions).Concat(Commands).Concat(Views).Concat(Pages).Concat(Roles);

    public IdentityEntry? Find(string blueprintId) =>
        All.FirstOrDefault(e => e.BlueprintId == blueprintId);
}

/// <summary>
/// Something lowering could not do, named against the blueprint element that caused it.
///
/// <para>Lowering never degrades quietly. A definition that silently lost a workflow because one
/// transition would not lower is indistinguishable from an app that never had one, and the user
/// approved the version with it.</para>
/// </summary>
/// <param name="ElementId">The blueprint element id, so the wizard can point at the right card.</param>
/// <param name="Layer">Which spec layer it belongs to, for routing a correction.</param>
/// <param name="Message">What could not be done, in the user's terms where possible.</param>
public sealed record LoweringDiagnostic(string ElementId, string Layer, string Message)
{
    public override string ToString() => $"{Layer}[{ElementId}]: {Message}";
}

/// <summary>What <see cref="BlueprintLowering.ToDefinition"/> produced.</summary>
/// <param name="Definition">The App Definition. Valid only when <paramref name="Diagnostics"/> is empty.</param>
public sealed record LoweringResult(
    JsonObject Definition,
    DefinitionIdentityMap Map,
    IReadOnlyList<LoweringDiagnostic> Diagnostics)
{
    public bool Ok => Diagnostics.Count == 0;
}
