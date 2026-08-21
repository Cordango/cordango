// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// Bring an existing App Definition into this workspace as source.
///
/// <para><b>The missing half of <c>publish</c>.</b> Publishing takes source and gives an instance a
/// definition; there was no way back, so an application that existed as a definition — exported from
/// an instance, written by an agent, handed over by somebody else — could not be opened, read or
/// edited as files at all. That is a strange gap in a product whose whole claim is that your
/// application is source you own.</para>
///
/// <para><b>It writes SOURCE, not a copy of the input.</b> The definition is imported into the
/// semantic model and then written back out through <see cref="CordSource"/>, which is the same
/// partition every other command reads and writes. So what lands on disk is what an author would
/// have written, split one file per aggregate, rather than a 90 KB document with a different
/// extension.</para>
///
/// <para><b>Where Cord does not model something yet, the fragment comes through raw</b> and is
/// visible in the file. That is the honest rendering: the alternative is dropping what cannot be
/// expressed, which loses screens silently. The raw-fragment allow-list in the test suite is the
/// running count of how much of the language is still in that state.</para>
/// </summary>
public static class ImportCommand
{
    public static int Run(Args args, Output output)
    {
        if (args.First is not { Length: > 0 } path)
            return output.Usage(
                "cordango import <app.definition.json> [--app <name>] — e.g. `cordango import expenses.json`");

        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
            return output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? ["Run `cordango new <app>` in an empty directory to start one."] : [],
                code: ExitCodes.NoWorkspace);

        if (!File.Exists(path))
            return output.Fail($"no such file: {path}", []);

        JsonNode? definition;
        try
        {
            definition = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return output.Fail($"{path} could not be read as JSON", [ex.Message]);
        }

        if (definition is not JsonObject document)
            return output.Fail($"{path} is not an App Definition", ["the top level is not an object"]);

        // Gated BEFORE import, not after. Importing a document that was never a valid definition
        // produces a model that is wrong in ways the resulting source cannot explain, and the
        // person is then debugging generated YAML instead of the file they actually have.
        var errors = Gate.Validate(document);
        if (errors.Count > 0)
            return output.Fail($"{path} is not a valid App Definition", errors);

        var key = (string?)document["key"];
        if (string.IsNullOrWhiteSpace(key))
            return output.Fail($"{path} has no key", ["an App Definition's key is its identity"]);

        var appName = args.Value("app") is { Length: > 0 } named ? named : key.Replace('_', '-');
        var appPath = $"apps/{appName}";
        var directory = workspace.DirectoryOf(appPath);

        if (Directory.Exists(directory))
            return output.Fail($"{appPath} already exists", [
                "Importing does not merge: it would have to guess which of two versions of an "
                + "aggregate is the one you meant. Delete the directory, or pass --app <name> to "
                + "import alongside it."]);

        if (workspace.Apps.Contains(appPath, StringComparer.Ordinal))
            return output.Fail($"{appPath} is registered but its directory is missing", [
                $"Remove it from the `apps` array in {WorkspaceFile.FileName}, or restore the directory."]);

        CordApp app;
        try
        {
            app = CordImport.Import(document);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or ArgumentException)
        {
            return output.Fail($"{path} could not be read into the semantic model", [ex.Message]);
        }

        var files = CordSource.Write(app);
        var loaded = new LoadedApp(appPath, directory, null, new Dictionary<string, string>(StringComparer.Ordinal), []);
        var diff = AppFolder.Save(loaded, files);

        // Files first, registration second: an unregistered directory is a doctor finding, while a
        // registration pointing at nothing is a workspace that no longer loads.
        (workspace with { Apps = [.. workspace.Apps, appPath] }).Save();

        // Checked through the same pipeline everything else uses, and reported rather than asserted:
        // a definition that was coherent stays coherent, and if it does not, that is a round-trip
        // defect the person importing should see immediately rather than at their next build.
        var report = Pipeline.Check(AppFolder.Load(workspace.Root, appPath));

        return output.Ok(
            new JsonObject
            {
                ["app"] = app.Key,
                ["path"] = appPath,
                ["files"] = diff.WrittenPaths.Count,
                ["check"] = report.ToJson(),
            },
            w =>
            {
                w.WriteLine($"Imported {app.Key} into {appPath}/ as {diff.WrittenPaths.Count} source files.");
                if (!report.Coherent)
                    foreach (var error in report.Errors) w.WriteLine($"  PROBLEM: {error}");
                else if (!report.Valid)
                    w.WriteLine("  checks: ok, incomplete");
                else
                    w.WriteLine("  checks: ok");
            });
    }
}
