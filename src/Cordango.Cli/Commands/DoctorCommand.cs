// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Problems that are not source errors: the workspace itself being wrong.
///
/// <para><b>Deliberately narrow in this slice.</b> CordyOSS §2 describes <c>doctor</c> as also
/// checking the binary architecture, available ports, the managed database payload and browser
/// launch — every one of which belongs to the local runtime, which does not exist yet. Reporting
/// green on checks that were never run would make the command actively harmful the first time
/// somebody trusts it.</para>
///
/// <para>What it does check is the class of problem that produces a confusing error somewhere else:
/// an app directory that exists but is not registered (so it silently does not build), one that is
/// registered but missing, and duplicate keys (so two apps fight over one identity).</para>
/// </summary>
public static class DoctorCommand
{
    public static int Run(Args args, Output output)
    {
        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
        {
            return output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? ["Run `cordango new <app-name>` in an empty directory to create one."] : [],
                code: ExitCodes.NoWorkspace);
        }

        var findings = new List<string>();
        var apps = workspace.Apps.Select(p => AppFolder.Load(workspace.Root, p)).ToList();

        if (string.IsNullOrWhiteSpace(workspace.WorkspaceId))
            findings.Add($"{WorkspaceFile.FileName}: no workspaceId — local runtime data cannot be "
                + "anchored to this workspace");

        // Registered but absent.
        findings.AddRange(apps.Where(a => !Directory.Exists(a.Directory))
            .Select(a => $"{a.Path}: registered in {WorkspaceFile.FileName} but the directory is missing"));

        // Present but unregistered. Reported, never installed — CordyOSS §6 is explicit that an
        // accidental folder under apps/ must not become part of the installation by existing.
        var appsRoot = Path.Combine(workspace.Root, "apps");
        if (Directory.Exists(appsRoot))
        {
            var registered = workspace.Apps
                .Select(p => Path.GetFullPath(workspace.DirectoryOf(p)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            findings.AddRange(Directory.EnumerateDirectories(appsRoot)
                .Where(d => !registered.Contains(Path.GetFullPath(d)))
                .Select(d => $"apps/{Path.GetFileName(d)}: not registered in {WorkspaceFile.FileName} "
                    + "— it will not be built or run"));
        }

        // Duplicate keys. The folder name is descriptive; the key is the identity, so two apps
        // claiming one key is a collision even when the directories are obviously different.
        findings.AddRange(apps
            .Where(a => a.App is not null)
            .GroupBy(a => a.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"two apps claim the key '{g.Key}': {string.Join(", ", g.Select(a => a.Path))}"));

        // Key that disagrees with its folder. A warning rather than an error — CordyOSS §3.6 says a
        // file move must not rename anything — but worth saying, because it is usually a typo.
        findings.AddRange(apps
            .Where(a => a.App is not null
                && !string.Equals(a.Key, Path.GetFileName(a.Path).Replace('-', '_'), StringComparison.Ordinal))
            .Select(a => $"{a.Path}: the directory name does not match the app key '{a.Key}' "
                + "(harmless — the key is authoritative)"));

        var payload = new JsonObject
        {
            ["workspace"] = workspace.Name,
            ["root"] = workspace.Root,
            ["apps"] = apps.Count,
            ["findings"] = new JsonArray([.. findings.Select(f => (JsonNode)f!)]),
        };

        if (findings.Count > 0)
            return output.Fail($"{findings.Count} finding{(findings.Count == 1 ? "" : "s")}", findings, payload);

        return output.Ok(payload, w =>
        {
            w.WriteLine($"{workspace.Name}: {apps.Count} app{(apps.Count == 1 ? "" : "s")}, no problems");
            w.WriteLine("note: the local runtime and its database are not built yet, so nothing about "
                + "running the app was checked.");
        });
    }
}
