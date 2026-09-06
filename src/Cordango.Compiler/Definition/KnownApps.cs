// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition;

/// <summary>One app a definition may point at, in the only terms the gate needs: what it is called,
/// what it holds, and what it announces.</summary>
/// <param name="Key">The value that goes in a field's <c>targetApp</c> — a core app's systemKey or
/// another app's definition key. Never a handle.</param>
/// <param name="EntityKeys">Its entity keys, so a typo'd <c>targetEntity</c> fails at check time
/// rather than as a blank column after the app is built.</param>
/// <param name="AnnouncedEvents">The event names its commands declare in <c>emits</c>, plus the
/// <c>&lt;entity&gt;.created|updated|deleted</c> names every write produces. What a subscription in
/// another app is allowed to name.</param>
public sealed record KnownApp(
    string Key,
    string Name,
    IReadOnlyList<string> EntityKeys,
    IReadOnlyList<string> AnnouncedEvents)
{
    public KnownApp(string key, string name, IReadOnlyList<string> entityKeys)
        : this(key, name, entityKeys, []) { }
}

/// <summary>
/// The other apps a definition is allowed to name, as the caller knows them.
///
/// <para><b>Three states, not two, and the third is the one that matters.</b></para>
///
/// <para><see cref="Unknown"/> — no roster at all. A cross-app key is accepted unchecked, which is
/// what the gate did before this type existed. The gate is a single-document function and genuinely
/// cannot see a tenant's apps, so a caller that cannot supply a roster must not have its references
/// refused.</para>
///
/// <para><see cref="InWorkspace"/> — a CLOSED set. Every app that will ever sit beside this one is
/// here, because a workspace is a repository somebody is looking at. An unrecognised key is a typo
/// and is refused.</para>
///
/// <para><see cref="InTenant"/> — an OPEN set. It can say what IS installed, and it cannot say what
/// will be installed tomorrow. So an unrecognised key is "not yet", never "wrong": refusing it would
/// mean an app that names a companion could only ever be installed after that companion, which makes
/// every app in a connected suite un-installable on its own. The write path still fails closed —
/// <c>data.reference_app_missing</c> — so nothing is trusted, only unrefused.</para>
///
/// <para>Core apps are always folded in: they ship with the platform, so they are known wherever this
/// runs, and a roster that omitted them would refuse the references that already work.</para>
/// </summary>
public sealed class KnownApps
{
    /// <summary>No roster: cross-app keys are accepted unchecked. The default, and what a caller that
    /// cannot see the other apps must use.</summary>
    public static readonly KnownApps Unknown = new(null, complete: false);

    private readonly Dictionary<string, KnownApp>? _byKey;

    private KnownApps(Dictionary<string, KnownApp>? byKey, bool complete)
    {
        _byKey = byKey;
        Complete = complete;
    }

    /// <summary>The apps of one WORKSPACE — a closed set, so a key that is not here is a mistake.
    /// Core apps are added automatically; an entry that repeats a core systemKey is ignored in its
    /// favour, because the platform's copy is the one that will actually be provisioned.</summary>
    public static KnownApps InWorkspace(IEnumerable<KnownApp> apps) => Build(apps, complete: true);

    /// <summary>The apps a TENANT has installed — an open set. Resolves what is there and tolerates
    /// what is not, because install order is a fact of life and every app in a suite is meant to be
    /// installable on its own.</summary>
    public static KnownApps InTenant(IEnumerable<KnownApp> apps) => Build(apps, complete: false);

    /// <inheritdoc cref="InWorkspace"/>
    public static KnownApps Of(IEnumerable<KnownApp> apps) => InWorkspace(apps);

    private static KnownApps Build(IEnumerable<KnownApp> apps, bool complete)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var byKey = new Dictionary<string, KnownApp>(StringComparer.Ordinal);
        foreach (var app in apps)
            if (app.Key is { Length: > 0 } && !CoreAppRegistry.IsCoreKey(app.Key))
                byKey[app.Key] = app;
        foreach (var core in CoreAppRegistry.All)
            byKey[core.SystemKey] = new KnownApp(core.SystemKey, core.Name, [.. core.EntityKeys]);
        return new KnownApps(byKey, complete);
    }

    /// <summary>True when this roster can answer "is there an app called x" at all.</summary>
    public bool Known => _byKey is not null;

    /// <summary>True when a key this roster does not hold is genuinely absent rather than merely not
    /// installed yet. Only a workspace can say that.</summary>
    public bool Complete { get; }

    public KnownApp? Find(string? key) =>
        key is not null && _byKey is not null && _byKey.TryGetValue(key, out var app) ? app : null;

    /// <summary>Every key a <c>targetApp</c> may name, for the "known: …" half of an error. Empty when
    /// this roster knows nothing, in which case nothing is being refused and nobody asks.</summary>
    public IEnumerable<string> Keys =>
        _byKey is null ? [] : _byKey.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
