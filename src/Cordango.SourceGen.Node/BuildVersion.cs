// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;

namespace Cordango.SourceGen.NodeVue;

/// <summary>
/// One version number, from the build, for everything in this repository that ships together.
///
/// <para>It is the version stamped into every application's build metadata AND the version of
/// <c>@cordango/standalone</c> the emitted <c>package.json</c> depends on — they are published from
/// one tag and cannot be installed apart, so they must not be two constants somebody remembers to
/// bump. The answer comes from <c>AssemblyInformationalVersion</c>, which MSBuild sets from the
/// project's <c>&lt;Version&gt;</c> and the release workflow overrides with the git tag.</para>
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
