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
/// <para><b>Absence is not emptiness.</b> A gate call with no roster (<see cref="Unknown"/>, and the
/// default for every caller that does not pass one) accepts an unrecognised <c>targetApp</c> without
/// checking it — which is what the gate did for every cross-app key before this type existed. That
/// asymmetry is the whole point: the gate is a single-document function and genuinely cannot see a
/// tenant's installed apps, so a caller that cannot supply the roster must not have its references
/// rejected, and a caller that CAN supply it gets a typo caught at check time. `Of([])` is the
/// caller saying "I looked and there are none" and does refuse.</para>
///
/// <para>Core apps are always folded in: they ship with the platform, so they are known wherever this
/// runs, and a roster that omitted them would refuse the references that already work.</para>
/// </summary>
public sealed class KnownApps
{
    /// <summary>No roster: cross-app keys are accepted unchecked. The default, and what a caller that
    /// cannot see the other apps must use.</summary>
    public static readonly KnownApps Unknown = new(null);

    private readonly Dictionary<string, KnownApp>? _byKey;

    private KnownApps(Dictionary<string, KnownApp>? byKey) => _byKey = byKey;

    /// <summary>A roster the gate will hold references to. Core apps are added automatically; an entry
    /// that repeats a core systemKey is ignored in its favour, because the platform's copy is the one
    /// that will actually be provisioned.</summary>
    public static KnownApps Of(IEnumerable<KnownApp> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var byKey = new Dictionary<string, KnownApp>(StringComparer.Ordinal);
        foreach (var app in apps)
            if (app.Key is { Length: > 0 } && !CoreAppRegistry.IsCoreKey(app.Key))
                byKey[app.Key] = app;
        foreach (var core in CoreAppRegistry.All)
            byKey[core.SystemKey] = new KnownApp(core.SystemKey, core.Name, [.. core.EntityKeys]);
        return new KnownApps(byKey);
    }

    /// <summary>True when this roster can answer "is there an app called x" at all.</summary>
    public bool Known => _byKey is not null;

    public KnownApp? Find(string? key) =>
        key is not null && _byKey is not null && _byKey.TryGetValue(key, out var app) ? app : null;

    /// <summary>Every key a <c>targetApp</c> may name, for the "known: …" half of an error. Empty when
    /// this roster knows nothing, in which case nothing is being refused and nobody asks.</summary>
    public IEnumerable<string> Keys =>
        _byKey is null ? [] : _byKey.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
