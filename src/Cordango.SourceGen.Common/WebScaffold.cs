// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text;

namespace Cordango.SourceGen.Common;

/// <summary>
/// The Vue shell every standalone target ships: the files under <c>web/</c> before a single page is
/// generated into them.
///
/// <para><b>One tree, every target.</b> The shell is a REST client — it talks to whatever answers
/// the HTTP contract and never asks what the backend is written in. Keeping it here rather than in
/// each target's scaffold is what makes "the same definition renders the same screens on every
/// stack" a property of the build instead of a promise two copies would have to keep.</para>
///
/// <para><b>Raw, not substituted.</b> Each target's scaffold merges these files into its own list
/// and runs its own token pass, because the tokens a web file carries (<c>{{AppName}}</c>,
/// <c>{{WebControlsVersion}}</c>) are a subset of the target's set and two substitution pipelines
/// would drift. What this class owns is the reading: separator normalisation, line-ending
/// normalisation, the ordinal order — the properties the determinism tests measure.</para>
/// </summary>
public static class WebScaffold
{
    private const string Prefix = "WebTemplate/";

    /// <summary>
    /// The version of <c>@cordango/web-controls</c> a generated application's front end is built
    /// against.
    ///
    /// <para>NOT the generator's own version. The runtime and the generator are one .NET solution
    /// published from one tag; the controls are a separate npm package on its own release line, so
    /// pinning them to the build version would name a version that has never existed on the
    /// registry.</para>
    ///
    /// <para>Exact, with no range operator. A caret would let <c>npm install</c> pick up a control
    /// this generator has never emitted against, in an application somebody generated months ago,
    /// on a machine where the only thing that changed was the day.</para>
    /// </summary>
    public static string WebControlsVersion => "0.1.5-alpha";

    /// <summary>The shell before substitution: target path (<c>web/…</c>) to content, ordinal by
    /// path. Read once — the resources cannot change while the process is running.</summary>
    public static IReadOnlyList<(string Path, string Content)> Files => Lazy.Value;

    private static readonly Lazy<IReadOnlyList<(string Path, string Content)>> Lazy =
        new(Load, isThreadSafe: true);

    private static IReadOnlyList<(string, string)> Load()
    {
        var assembly = typeof(WebScaffold).Assembly;
        var files = new List<(string Path, string Content)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            // MSBuild's RecursiveDir uses the BUILD machine's separator. Normalising here is what
            // keeps a Windows-built CLI and a Linux-built one emitting the same paths.
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            var target = normalised[Prefix.Length..];
            if (target.EndsWith(".template", StringComparison.Ordinal))
                target = target[..^".template".Length];

            files.Add((target, Read(assembly, name)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded web scaffold resource '{name}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Line endings normalised on the way IN, before anything is hashed or written, so a scaffold
        // cloned on Windows and one cloned on Linux are the same scaffold.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
