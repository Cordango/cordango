// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;

namespace Cordango.SourceGen.DotNetVue;

/// <summary>
/// One version number, from the build, for everything in this repository that ships together.
///
/// <para><b>There were four of them and they had already diverged.</b> The CLI reported
/// 0.1.0-alpha while installing as 0.3.0-alpha; the generator stamped 0.1.0-alpha into every
/// application's build metadata; and — the one that was an actual bug — the emitted project file
/// referenced <c>Cordango.Standalone</c> at 0.1.0-alpha, a version that does not exist on the feed.
/// Anybody generating with the package shape would have hit a restore failure naming a package they
/// had never heard of.</para>
///
/// <para>Every one of those was a constant somebody had to remember to bump, which is the same
/// failure mode <see cref="Scaffold.Version"/> already avoids by hashing the files instead. Here the
/// answer comes from <c>AssemblyInformationalVersion</c>, which MSBuild sets from the project's
/// <c>&lt;Version&gt;</c> and the release workflow overrides with the git tag — so the tag that
/// published the packages is the number in the metadata, and they cannot disagree.</para>
/// </summary>
internal static class BuildVersion
{
    /// <summary>This build, without the commit SourceLink appends. Useful in a package listing,
    /// noise in generated output — and it would make the output non-deterministic across otherwise
    /// identical builds of the same source from two checkouts.</summary>
    public static readonly string Current = Read();

    private static string Read()
    {
        var informational = typeof(BuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0-local";

        var build = informational.IndexOf('+', StringComparison.Ordinal);
        return build < 0 ? informational : informational[..build];
    }
}
