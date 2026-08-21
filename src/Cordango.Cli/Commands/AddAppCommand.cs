// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Templates;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Add another app to this workspace and register it.
///
/// <para><b>Registration is what installs an app, not the directory existing</b> (CordyOSS §6). The
/// files and <c>cordango.yaml</c> are written together here; a folder that appears under <c>apps/</c> by
/// some other route is reported by <c>doctor</c> and otherwise ignored.</para>
/// </summary>
public static class AddAppCommand
{
    public static int Run(Args args, Output output)
    {
        if (args.First is not { Length: > 0 } appName)
            return output.Usage("cordango add app <name> — e.g. `cordango add app crm`");

        if (appName.Contains('/') || appName.Contains('\\') || appName.Contains(".."))
            return output.Usage($"'{appName}' is a path, not an app name — apps are created under apps/");

        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
        {
            return output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? [$"Run `cordango new {appName}` in an empty directory to start one."] : [],
                code: ExitCodes.NoWorkspace);
        }

        var appPath = $"apps/{appName}";
        if (workspace.Apps.Contains(appPath, StringComparer.Ordinal))
            return output.Fail($"{appPath} is already registered", []);

        if (Directory.Exists(workspace.DirectoryOf(appPath)))
            return output.Fail($"{appPath} already exists but is not registered",
                [$"Add \"{appPath}\" to the `apps` array in {WorkspaceFile.FileName} to install it, "
                    + "or move the directory out of the way."]);

        // The key collides even when the directory does not: two apps sharing a key would fight over
        // one identity in every cross-app reference.
        var key = Scaffold.KeyFor(appName);
        var existing = workspace.Apps
            .Select(p => AppFolder.Load(workspace.Root, p))
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

        if (existing is not null)
            return output.Fail($"the key '{key}' is already used by {existing.Path}",
                ["App keys are the durable identity — pick a different name."]);

        if (AppScaffolder.Create(workspace.Root, appName, out var report) is { } failure)
            return output.Fail("could not create the app", [failure]);

        // Files first, registration second: an unregistered directory is a doctor finding, while a
        // registration pointing at nothing is a workspace that no longer loads.
        (workspace with { Apps = [.. workspace.Apps, appPath] }).Save();

        return output.Ok(
            new JsonObject
            {
                ["app"] = report!.AppKey,
                ["path"] = appPath,
                ["check"] = report.ToJson(),
            },
            w =>
            {
                w.WriteLine($"Added {report.AppKey} in {appPath}/ and registered it in {WorkspaceFile.FileName}.");
                w.WriteLine($"  cordango check --app {report.AppKey}");
            });
    }
}
