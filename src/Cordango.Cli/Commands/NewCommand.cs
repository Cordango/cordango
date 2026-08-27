// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cordango.Cli.Templates;
using Cordango.Cli.Workspace;
using Cordango.Cord;

namespace Cordango.Cli.Commands;

/// <summary>
/// Creating an app's files — shared by <c>cordango new</c> and <c>cordango add app</c>, because the two
/// differ only in whether a workspace already exists around it.
/// </summary>
internal static class AppScaffolder
{
    /// <summary>
    /// Builds the starter app through the real pipeline and writes it.
    ///
    /// <para>Validation is not a formality here. If the starter operations ever stop producing a
    /// coherent app — a schema move, a vocabulary change — this fails at <c>cordango new</c> with the
    /// gate's own errors, which is a bug report. The alternative is handing somebody a broken
    /// workspace and letting them discover it as their first experience of the product.</para>
    /// </summary>
    /// <returns>Null on success; the failure message otherwise.</returns>
    public static string? Create(string root, string appDirectoryName, out CheckReport? report)
    {
        report = null;

        var key = Scaffold.KeyFor(appDirectoryName);
        var label = Scaffold.LabelFor(appDirectoryName);
        var appPath = $"apps/{appDirectoryName}";

        var identity = new CordApp(Key: key, Name: label, Version: "0.1.0",
            Description: $"{label}, created by cordango new. Change or replace it.");

        var prepared = CordTransaction.Prepare(identity, Scaffold.StarterOps());
        if (!prepared.Ok)
            return "the starter app did not apply: "
                + string.Join("; ", prepared.Errors.Select(AppFolder.Describe));

        var checkReport = Pipeline.Check(prepared.Next, key, appPath, prepared.Map);
        if (!checkReport.Coherent)
            return "the starter app is not coherent: " + string.Join("; ", checkReport.Errors);

        var directory = Path.Combine(root, "apps", appDirectoryName);
        Directory.CreateDirectory(directory);
        foreach (var file in CordSource.Write(prepared.Next))
            AtomicFile.Write(
                Path.Combine(directory,
                    (file.Path + AppFolder.Extension).Replace('/', Path.DirectorySeparatorChar)),
                Yaml.Write(file.Document));

        report = checkReport;
        return null;
    }
}

/// <summary>
/// Initialize one workspace repository and its first app.
///
/// <para><b>The empty-directory rule is deliberate</b> (CordyOSS §2). Scaffolding into a directory
/// that already holds something means guessing which of those things was meant to survive, and the
/// guess is wrong exactly when it matters. A <c>.git</c> directory is the one exception — running
/// <c>git init</c> before <c>cordango new</c> is a reasonable order to do things in, and refusing it
/// teaches nothing.</para>
/// </summary>
public static class NewCommand
{
    public static int Run(Args args, Output output)
    {
        if (args.First is not { Length: > 0 } appName)
            return output.Usage("cordango new <first-app> — the name of the first app, e.g. `cordango new expense-approval`");

        if (appName.Contains('/') || appName.Contains('\\') || appName.Contains(".."))
            return output.Usage($"'{appName}' is a path, not an app name — apps are created under apps/");

        var root = Directory.GetCurrentDirectory();

        var occupants = Directory.EnumerateFileSystemEntries(root)
            .Select(Path.GetFileName)
            .Where(n => n is not ".git")
            .ToList();

        if (occupants.Count > 0)
        {
            // Both refusals name `add app`, because the question somebody actually has at this
            // moment is "how do I get a second app" — and the answer is the same either way. Saying
            // only "needs an empty directory" reads as though a workspace holds one app, which is
            // the opposite of the design.
            var existing = File.Exists(Path.Combine(root, WorkspaceFile.FileName));
            return output.Fail(
                existing
                    ? "this is already a Cord workspace"
                    : "cordango new creates a NEW workspace, so it needs an empty directory",
                existing
                    ? [$"To add another app here, run `cordango add app {appName}`."]
                    : [$"{occupants.Count} entr{(occupants.Count == 1 ? "y" : "ies")} here: "
                        + string.Join(", ", occupants.Take(5)),
                       "One workspace holds MANY apps: create it in an empty directory, then "
                        + "`cordango add app <name>` for each one after the first."]);
        }

        var workspaceName = Scaffold.LabelFor(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
        var appPath = $"apps/{appName}";

        if (AppScaffolder.Create(root, appName, out var report) is { } failure)
            return output.Fail("could not create the app", [failure]);

        new WorkspaceFile(
            root,
            WorkspaceFile.NewId(DateTimeOffset.UtcNow, RandomNumberGenerator.GetBytes),
            workspaceName,
            WorkspaceFile.DefaultRuntime,
            [appPath]).Save();

        AtomicFile.Write(Path.Combine(root, ".gitignore"), Scaffold.GitIgnore + "\n");
        AtomicFile.Write(Path.Combine(root, ".gitattributes"), Scaffold.GitAttributes + "\n");
        AtomicFile.Write(Path.Combine(root, "README.md"), Scaffold.Readme(workspaceName, appPath) + "\n");

        var instructions = Scaffold.AgentInstructions(workspaceName) + "\n";
        AtomicFile.Write(Path.Combine(root, "CLAUDE.md"), instructions);
        AtomicFile.Write(Path.Combine(root, "AGENTS.md"), instructions);

        return output.Ok(
            new JsonObject
            {
                ["workspace"] = workspaceName,
                ["root"] = root,
                ["app"] = report!.AppKey,
                ["path"] = appPath,
                ["check"] = report.ToJson(),
            },
            w =>
            {
                Branding.Write(w);
                w.WriteLine($"Created {workspaceName} with one app, {report.AppKey}, in {appPath}/");
                w.WriteLine();
                w.WriteLine("  cordango check                 " + Ansi.Dim("# it already passes"));
                w.WriteLine("  cordango inspect --app " + report.AppKey);
                w.WriteLine("  cordango configure             "
                    + Ansi.Dim("# where these apps run. `cordango build` then needs no flags"));
                w.WriteLine();
                w.WriteLine("Open this directory in Claude Code, Codex or Cursor — CLAUDE.md and "
                    + "AGENTS.md explain the workflow. Ask it what the app should do before you ask "
                    + "it to build one.");
            });
    }
}
