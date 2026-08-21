// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
    /// <summary>Pre-alpha. The local runtime does not exist yet, so nothing can depend on this
    /// number meaning "installable".</summary>
    public const string CliVersion = "0.1.0-alpha";

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
