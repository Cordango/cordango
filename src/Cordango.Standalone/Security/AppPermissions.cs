// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Security;

/// <summary>One role's opinion about one field: null means "this role said nothing, use the
/// entity-level default".</summary>
/// <param name="Field">The field key, as the App Definition spells it.</param>
/// <param name="Read">May this role see the field at all.</param>
/// <param name="Update">May this role write it.</param>
public sealed record FieldOverride(string Field, bool? Read = null, bool? Update = null);

/// <summary>What one role may do to one entity. <c>Entity</c> is a key, or <c>"*"</c> for the
/// catch-all a role falls back to when it names no grant for the entity in hand.</summary>
public sealed record EntityGrant(
    string Entity,
    bool Create = false,
    bool Read = false,
    bool Update = false,
    bool Delete = false,
    IReadOnlyList<FieldOverride>? FieldOverrides = null,
    IReadOnlyList<string>? Commands = null)
{
    public IReadOnlyList<FieldOverride> FieldOverrides { get; init; } = FieldOverrides ?? [];

    /// <summary>Command keys this grant unlocks. Deny-by-default: a command absent from every one of
    /// the caller's grants cannot be run, even by a role that may update the entity freely.
    /// <c>"*"</c> means all of them.</summary>
    public IReadOnlyList<string> Commands { get; init; } = Commands ?? [];
}

/// <summary>A named role and the grants it carries.</summary>
public sealed record RoleDefinition(string Key, IReadOnlyList<EntityGrant> Grants);

/// <summary>
/// The application's <c>roles</c> block, as typed data rather than JSON.
///
/// <para>The generator emits one static instance of this from the compiled definition, so the rules
/// are compiled into the application: readable, greppable, and impossible to edit into a state the
/// definition never described without editing C#.</para>
/// </summary>
public sealed class AppPermissions
{
    public AppPermissions(IReadOnlyList<RoleDefinition> roles) => Roles = roles;

    public IReadOnlyList<RoleDefinition> Roles { get; }

    /// <summary>An application whose definition declares no roles at all.</summary>
    public static readonly AppPermissions None = new([]);

    /// <summary>True when the definition declared no roles. Such an application has decided nothing
    /// about access, and <see cref="PermissionResolver"/> answers read-only rather than inventing a
    /// permission model on its behalf.</summary>
    public bool DeclaresNoRoles => Roles.Count == 0;
}
