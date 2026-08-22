// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// Four versions, reported separately because they move independently and a bug report that
/// conflates them is unactionable.
/// </summary>
public static class VersionCommand
{
    /// <summary>
    /// This build's version, read from the assembly rather than typed here.
    ///
    /// <para>It was a constant, and it drifted the moment the CLI became a package: the tool
    /// installed as 0.3.0-alpha and reported 0.1.0-alpha. That is worse than untidy — the number is
    /// written into every generated application's <c>cordango.build.json</c>, so "which version of
    /// the toolchain produced this?" was being answered wrongly and permanently.</para>
    ///
    /// <para><c>AssemblyInformationalVersion</c> is set by MSBuild from the project's
    /// <c>&lt;Version&gt;</c>, and the release workflow overrides that with the git tag — so the
    /// number reported here, the number on the package and the tag that published it cannot
    /// disagree. SourceLink appends <c>+&lt;commit&gt;</c>, which is trimmed: useful in a package
    /// listing, noise in a version line.</para>
    /// </summary>
    public static readonly string CliVersion = Read();

    private static string Read()
    {
        var informational = typeof(VersionCommand).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "0.0.0-local";

        var build = informational.IndexOf('+', StringComparison.Ordinal);
        return build < 0 ? informational : informational[..build];
    }

    public static int Run(Output output) => output.Ok(
        new JsonObject
        {
            ["cordango"] = CliVersion,
            ["sourceFormat"] = WorkspaceFile.FormatVersion,
            ["appDefinitionSchema"] = AppSchemaVersion.Current,
        },
        w =>
        {
            w.WriteLine($"cordango                {CliVersion}");
            w.WriteLine($"source format           {WorkspaceFile.FormatVersion}");
            w.WriteLine($"App Definition schema   {AppSchemaVersion.Current}");
        });
}
