// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cord;

/// <summary>The aggregate kinds a co-creation scope can name (contract CF-3). Named once so the
/// scope checks, the file splitter and the storage layer cannot disagree about what an aggregate
/// is.</summary>
public static class CordAggregateKinds
{
    /// <summary>The app's identity — key, name, version. No operation writes it: identity is seeded
    /// by the host when the app is created, so a scope of this kind admits nothing.</summary>
    public const string Identity = "identity";

    /// <summary>The data model: entities and their fields.</summary>
    public const string Domain = "domain";

    /// <summary>Lifecycles, actions and automations — what the app DOES.</summary>
    public const string Behaviour = "behaviour";

    /// <summary>Roles and their grants — who may do any of it.</summary>
    public const string Access = "access";

    /// <summary>One screen, by key.</summary>
    public const string Screen = "screen";

    /// <summary>One named tab of one screen, keyed <c>&lt;screenKey&gt;/&lt;tabKey&gt;</c>.</summary>
    public const string Tab = "tab";

    public static readonly IReadOnlyList<string> All =
        [Identity, Domain, Behaviour, Access, Screen, Tab];
}

/// <summary>
/// One aggregate, named. The unit a co-creation candidate is scoped to, and the unit a
/// <c>.cord</c> file holds.
/// </summary>
/// <param name="Kind">One of <see cref="CordAggregateKinds.All"/>.</param>
/// <param name="Key">Null for the collection-shaped scopes (identity, domain, behaviour, access);
/// the screen key for <see cref="CordAggregateKinds.Screen"/>;
/// <c>&lt;screenKey&gt;/&lt;tabKey&gt;</c> for <see cref="CordAggregateKinds.Tab"/>. An op's TARGET
/// always carries a key when the op names one — the null-key form belongs to scopes.</param>
public sealed record CordAggregateRef(string Kind, string? Key = null)
{
    public override string ToString() => Key is null ? Kind : $"{Kind}:{Key}";
}

/// <summary>
/// The two questions the co-creation loop asks about every operation: which aggregate does it name,
/// and is that aggregate inside the open scope.
///
/// <para><b><see cref="Target"/> drives scope checks AND the file partition.</b> One derivation,
/// because an op that was admitted under one idea of "its aggregate" and filed under another would
/// let a screen edit land in another screen's file — the exact cross-aggregate bleed the scope
/// exists to prevent.</para>
/// </summary>
public static class CordAggregates
{
    /// <summary>The operation names a scope of this kind may use. Tool-level: the per-op aggregate
    /// check is <see cref="Admits"/>, which runs after parsing. Identity deliberately admits
    /// nothing — key, name and version are the host's facts, seeded rather than authored.</summary>
    public static IReadOnlyList<string> AllowedOps(CordAggregateRef scope) => scope.Kind switch
    {
        CordAggregateKinds.Domain => CordOps.DomainOpNames,
        // Behaviour minus roles: roles are the ACCESS aggregate (CF-3 separates them because the
        // co-creation journey reviews them separately). remove_behaviour stays in both lists — the
        // KIND being removed decides which aggregate it targets, and Admits enforces that.
        CordAggregateKinds.Behaviour =>
            ["upsert_lifecycle", "upsert_action", "upsert_automation", "remove_behaviour"],
        CordAggregateKinds.Access => ["upsert_role", "remove_behaviour"],
        CordAggregateKinds.Screen or CordAggregateKinds.Tab => CordOps.UiOpNames,
        _ => [],
    };

    /// <summary>The aggregate one parsed operation names.</summary>
    public static CordAggregateRef Target(CordOp op) => op switch
    {
        UpsertEntity ue => new(CordAggregateKinds.Domain, ue.Entity.Key),
        UpsertField uf => new(CordAggregateKinds.Domain, uf.Entity),
        RemoveEntity re => new(CordAggregateKinds.Domain, re.Entity),
        RemoveField rf => new(CordAggregateKinds.Domain, rf.Entity),
        UpsertLifecycle ul => new(CordAggregateKinds.Behaviour,
            $"{CordBehaviourKinds.Lifecycle}/{ul.Process.Entity}"),
        UpsertAction ua => new(CordAggregateKinds.Behaviour,
            $"{CordBehaviourKinds.Action}/{ua.Action.Key}"),
        UpsertAutomation um => new(CordAggregateKinds.Behaviour,
            $"{CordBehaviourKinds.Automation}/{um.Schedule.Key}"),
        UpsertRole ur => new(CordAggregateKinds.Access, ur.Role.Key),
        RemoveBehaviour rb => rb.Kind == CordBehaviourKinds.Role
            ? new(CordAggregateKinds.Access, rb.Key)
            : new(CordAggregateKinds.Behaviour, $"{rb.Kind}/{rb.Key}"),
        UpsertScreen us => new(CordAggregateKinds.Screen, us.Screen.Key),
        RemoveScreen rs => new(CordAggregateKinds.Screen, rs.Key),
        UpsertScreenTab ut => new(CordAggregateKinds.Tab, $"{ut.Screen}/{ut.Tab.Key}"),
        RemoveScreenTab rt => new(CordAggregateKinds.Tab, $"{rt.Screen}/{rt.Tab}"),
        _ => throw new InvalidOperationException($"no aggregate mapping for {op.GetType().Name}"),
    };

    /// <summary>
    /// Whether an operation targeting <paramref name="target"/> is inside <paramref name="scope"/>.
    ///
    /// <para>A collection scope (domain, behaviour, access) admits every aggregate of its kind — the
    /// co-creation phases review those concerns whole. A screen scope admits that one screen AND its
    /// named tabs, because a tab is a child of its screen the way a field is a child of its entity.
    /// A tab scope admits exactly that tab and nothing else — including not the whole-screen op,
    /// which would restate every other tab.</para>
    /// </summary>
    public static bool Admits(CordAggregateRef scope, CordAggregateRef target)
    {
        if (scope.Kind == target.Kind)
            return scope.Key is null || string.Equals(scope.Key, target.Key, StringComparison.Ordinal);

        // A screen scope reaches into its own tabs: tab keys are "<screen>/<tab>".
        if (scope.Kind == CordAggregateKinds.Screen && target.Kind == CordAggregateKinds.Tab
            && scope.Key is { Length: > 0 } screen && target.Key is { } tab)
            return tab.StartsWith(screen + "/", StringComparison.Ordinal);

        return false;
    }
}
