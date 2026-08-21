// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Entity <c>role</c> vocabulary — the parts an entity can play in an archetype the platform knows
/// how to operate. Today that is the Forms archetype (template → questions → submission → answers).
/// </summary>
public static class Archetypes
{
    /// <summary>The Forms roles whose entities are NOT their own subject.
    ///
    /// <para>Their records are written by the intake machinery — the public form submit creates a
    /// response and its answers server-side — and read through their template's surfaces, so they
    /// need neither a page of their own nor a create button. Anything reasoning about "an entity a
    /// person browses and creates" has to exclude them, which is why the set is here and not in
    /// whichever caller noticed first: <c>AppCompiler</c> uses it to keep them out of navigation,
    /// <c>DesignDefaults</c> to keep them out of the create-path floor and the candidate smoke
    /// checks. Three copies of this list would eventually disagree about helpdesk.</para></summary>
    public static readonly IReadOnlySet<string> SubordinateFormRoles =
        new HashSet<string>(StringComparer.Ordinal) { "formField", "formResponse", "formAnswer" };

    /// <summary>Whether the entity is embedded in something else rather than a subject of its own —
    /// it declares (or was inferred to have) an <c>ownedBy</c> parent, or it plays a child role in
    /// the Forms archetype.</summary>
    public static bool IsSubordinate(JsonObject entity) =>
        entity["ownedBy"] is JsonObject
        || (entity["role"] is JsonValue r && r.TryGetValue<string>(out var role)
            && SubordinateFormRoles.Contains(role));
}
