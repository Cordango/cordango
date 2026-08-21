// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.SourceGen;

/// <summary>
/// One axis of the vocabulary, split into what a target builds and what it deliberately does not.
///
/// <para><b>Withheld carries a REASON, and that is the whole design.</b> A capability model that
/// only lists what works answers "no" and stops. Somebody then reads the schema, sees the feature is
/// legal, and cannot tell whether the target forgot it, rejected it on purpose, or has it planned.
/// The reason turns a refusal into an explanation, and it is the same doctrine
/// <c>CordWordMap.Withheld</c> already applies to the authoring vocabulary.</para>
///
/// <para>It is also checkable: a test asserts that supported ∪ withheld covers everything the schema
/// allows, so a value added to the language cannot be quietly unhandled by a target that claims to
/// know its own limits.</para>
/// </summary>
/// <param name="Fallback">What to say about a value that is not in either list. Most axes are
/// closed vocabularies where that cannot happen; the open one is <c>targetApp</c>, where the value
/// is another application's key and could be anything. A target that knows why it can never resolve
/// one says so once here rather than leaving the validator to invent wording per call site.</param>
public sealed record CapabilitySet(
    IReadOnlySet<string> Supported,
    IReadOnlyDictionary<string, string> Withheld,
    string? Fallback = null)
{
    public static CapabilitySet Of(
        IEnumerable<string> supported, string? fallback = null,
        params (string Value, string Why)[] withheld) =>
        new(supported.ToHashSet(StringComparer.Ordinal),
            withheld.ToDictionary(w => w.Value, w => w.Why, StringComparer.Ordinal),
            fallback);

    public bool Allows(string value) => Supported.Contains(value);

    /// <summary>Why a value is not built, or null when the target has never heard of it. The two are
    /// different answers and a message should not blur them.</summary>
    public string? WhyNot(string value) => Withheld.TryGetValue(value, out var why) ? why : null;

    /// <summary>The message a diagnostic carries: the documented reason where there is one, the
    /// target's standing explanation where the value is outside any list it could enumerate, and an
    /// honest last resort otherwise.</summary>
    public string Explain(string value) =>
        WhyNot(value) ?? Fallback ?? $"'{value}' is not something this target knows how to build";

    public IReadOnlySet<string> Known =>
        Supported.Concat(Withheld.Keys).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// What a target can build, declared as data so that <c>cordango check --target</c> can answer
/// without generating anything — and so an out-of-process generator can answer the same question
/// over stdout.
/// </summary>
/// <param name="Blocks">Block kinds. The largest axis and the one most likely to be partial.</param>
/// <param name="Effects">Effect types a command or workflow may run.</param>
/// <param name="Triggers">Workflow trigger events.</param>
/// <param name="FieldTypes">Field types the target can store and render.</param>
/// <param name="PlatformTargets">Values of <c>targetApp</c> the target can resolve — for a
/// standalone application, the platform apps it carries a local equivalent of. Anything else is a
/// reference into a separately installed application and cannot be resolved at all.</param>
/// <param name="PlatformEntities">Values of <c>targetEntity</c> within those, same argument.</param>
/// <param name="SeriesAndPrev">Ordered series and <c>prev()</c>: a row whose value depends on the
/// row before it.</param>
/// <param name="WindowedRollups">Rollups with a <c>window</c> or <c>match</c>, i.e. aggregation that
/// is not simply "up the parent reference".</param>
public sealed record GeneratorCapabilities(
    CapabilitySet Blocks,
    CapabilitySet Effects,
    CapabilitySet Triggers,
    CapabilitySet FieldTypes,
    CapabilitySet PlatformTargets,
    CapabilitySet PlatformEntities,
    bool SeriesAndPrev,
    bool WindowedRollups);
