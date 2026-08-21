// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;

namespace Cordango.Standalone.Security;

/// <summary>
/// Turns the application's roles and the caller's role keys into an <see cref="EntityAccess"/>.
///
/// <para><b>This is the standalone application's OWN implementation, on purpose.</b> Cordango
/// Platform enforces the same four rules with its own code, and the two are deliberately not
/// shared. Sharing would create a coupling where a change made for one product's reasons quietly
/// alters the other's enforcement, and enforcement is the last place to accept a surprise. The rules
/// are small and finite, so the cost of two implementations is low.</para>
///
/// <para><b>Divergence is caught rather than prevented.</b> <c>tests/fixtures/permissions/</c> holds
/// decision fixtures — definition, roles, entity, field, operation, expected answer — checked into
/// this repository as data and asserted by both products' suites. If the two implementations ever
/// disagree, a test goes red instead of a production answer going wrong.</para>
///
/// <para>The four rules, in the order they matter:</para>
/// <list type="number">
/// <item>Roles union. Any role of the caller's that allows an operation allows it.</item>
/// <item>Within one role, a grant naming the entity beats that role's <c>*</c> wildcard.</item>
/// <item>A field's answer is that role's explicit override if it has one, else that role's
/// entity-level default — and only then unioned across roles.</item>
/// <item>Commands are deny-by-default and are never implied by update.</item>
/// </list>
/// </summary>
public static class PermissionResolver
{
    /// <summary>What this caller may do to this entity.</summary>
    public static EntityAccess Resolve(AppPermissions permissions, ICurrentUser user, string entityKey)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(user);

        if (user.IsAdministrator) return EntityAccess.Full;

        // Nobody signed in gets nothing, and this line comes BEFORE the no-roles default rather than
        // after it. An application whose definition declares no roles falls back to read-only, which
        // is a reasonable answer for a colleague and a terrible one for the internet: without this
        // check, deploying such an application would publish every row in it to anonymous callers,
        // and nothing in the definition would hint that it had.
        if (user.UserId is null) return EntityAccess.None;

        if (permissions.DeclaresNoRoles) return EntityAccess.ReadOnly;
        return Resolve(permissions, user.RoleKeys, entityKey);
    }

    /// <summary>The rule evaluation itself, over role keys alone. Separate from the caller so the
    /// decision fixtures can drive it directly, with no user object and no bypass in the way.</summary>
    public static EntityAccess Resolve(AppPermissions permissions, IReadOnlyCollection<string> roleKeys, string entityKey)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(roleKeys);

        if (roleKeys.Count == 0) return EntityAccess.None;

        var wanted = new HashSet<string>(roleKeys, StringComparer.Ordinal);

        // Rule 2, applied per role: the grant naming the entity, else the wildcard, else this role
        // has nothing to say here and drops out entirely.
        var grants = new List<EntityGrant>();
        foreach (var role in permissions.Roles)
        {
            if (!wanted.Contains(role.Key)) continue;
            if (EffectiveGrant(role, entityKey) is { } grant) grants.Add(grant);
        }

        if (grants.Count == 0) return EntityAccess.None;

        // Rule 1.
        var create = grants.Any(g => g.Create);
        var read = grants.Any(g => g.Read);
        var update = grants.Any(g => g.Update);
        var delete = grants.Any(g => g.Delete);

        // Rule 3. Note the shape: for EACH grant, resolve the field against THAT grant's default
        // before unioning. Unioning the defaults first and then applying overrides would let a role
        // that may read everything silently re-open a field another role hid.
        var fields = new Dictionary<string, FieldRule>(StringComparer.Ordinal);
        foreach (var field in grants.SelectMany(g => g.FieldOverrides).Select(o => o.Field).Distinct(StringComparer.Ordinal))
        {
            var fieldRead = grants.Any(g => Override(g, field)?.Read ?? g.Read);
            var fieldUpdate = grants.Any(g => Override(g, field)?.Update ?? g.Update);
            fields[field] = new FieldRule(fieldRead, fieldUpdate);
        }

        // Rule 4.
        var allCommands = grants.Any(g => g.Commands.Contains("*", StringComparer.Ordinal));
        var commands = grants
            .SelectMany(g => g.Commands)
            .Where(c => !string.Equals(c, "*", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        return new EntityAccess(create, read, update, delete, fields, commands, allCommands);
    }

    private static EntityGrant? EffectiveGrant(RoleDefinition role, string entityKey)
    {
        EntityGrant? specific = null, wildcard = null;
        foreach (var grant in role.Grants)
        {
            if (string.Equals(grant.Entity, entityKey, StringComparison.Ordinal)) specific = grant;
            else if (string.Equals(grant.Entity, "*", StringComparison.Ordinal)) wildcard ??= grant;
        }
        return specific ?? wildcard;
    }

    private static FieldOverride? Override(EntityGrant grant, string field)
    {
        foreach (var o in grant.FieldOverrides)
            if (string.Equals(o.Field, field, StringComparison.Ordinal))
                return o;
        return null;
    }
}
