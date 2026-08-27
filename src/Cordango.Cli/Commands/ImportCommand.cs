// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>One app as the instance lists it. Everything here is what the studio's own list shows,
/// because it is the same endpoint — the CLI does not get a private view of somebody's apps.</summary>
/// <param name="Id">The instance's id, and the only thing that fetches a definition.</param>
/// <param name="Handle">The address it is served at, and the name a person will type.</param>
public sealed record RemoteApp(string Id, string Handle, string Name, string Status, string Version, int Entities)
{
    public static RemoteApp? Of(JsonNode? node)
    {
        if (node is not JsonObject row) return null;
        if ((string?)row["id"] is not { Length: > 0 } id) return null;

        return new RemoteApp(
            id,
            (string?)row["handle"] ?? id,
            (string?)row["name"] ?? (string?)row["handle"] ?? id,
            (string?)row["status"] ?? "",
            (string?)row["version"] ?? "",
            (int?)row["entities"] ?? 0);
    }

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["handle"] = Handle,
        ["name"] = Name,
        ["status"] = Status,
        ["version"] = Version,
        ["entities"] = Entities,
    };

    /// <summary>Every spelling a person might reasonably type for this app.</summary>
    public bool Answers(string typed) =>
        string.Equals(Handle, typed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Id, typed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Name, typed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Handle.Replace('-', '_'), typed.Replace('-', '_'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Bring an existing App Definition into this workspace as source — from a file, or from the
/// instance this workspace is connected to.
///
/// <para><b>The missing half of <c>publish</c>.</b> Publishing takes source and gives an instance a
/// definition; there was no way back, so an application that existed as a definition — built in
/// Studio, exported from an instance, handed over by somebody else — could not be opened, read or
/// edited as files at all. That is a strange gap in a product whose whole claim is that your
/// application is source you own.</para>
///
/// <para><b>The file path stays entirely offline.</b> <c>cordango import expenses.json</c> touches
/// no network and needs no account, exactly as it always did; the instance is only reached when
/// there is no such file to read, or nothing was named at all. A command that quietly acquired a
/// network call on its existing spelling would be a worse trade than the feature is worth.</para>
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
    public static async Task<int> RunAsync(Args args, Output output, CancellationToken ct)
    {
        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
            return output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? ["Run `cordango new <app>` in an empty directory to start one."] : [],
                code: ExitCodes.NoWorkspace);

        var named = args.First;

        // A path that exists is a file import, decided before anything else looks at it. Checking
        // the filesystem first is also what makes `./support` unambiguous next to an app called
        // `support` — whatever is on disk wins, because that is what the person is looking at.
        if (named is { Length: > 0 } && File.Exists(named))
            return FromFile(named, args, output, workspace);

        if (named is { Length: > 0 } && Connection.Find(args, workspace) is null)
        {
            // Not a file, and no credential to try it as an app name with. "No such file" is still
            // the true answer here; the second line is the part that was missing.
            return output.Fail($"no such file: {named}",
            [
                "To import an app from a Cordango instance instead, connect this workspace first:",
                "  cordango login <token>",
                "  cordango import            # then pick from the apps you can reach",
            ]);
        }

        return await FromInstanceAsync(named, args, output, workspace, ct);
    }

    /// <summary>
    /// Pick one of the instance's apps and import it.
    ///
    /// <para><b>Two calls, and the list is the cheap one.</b> The list endpoint answers without
    /// definitions — a workspace of twenty apps would be megabytes to answer "which one" — so the
    /// definition is fetched for exactly the app that was chosen.</para>
    /// </summary>
    private static async Task<int> FromInstanceAsync(string? named, Args args, Output output,
        WorkspaceFile workspace, CancellationToken ct)
    {
        var login = Connection.Resolve(args, workspace, output, out var offline);
        if (login is null)
        {
            output.Note("");
            output.Note("A file needs no connection at all: `cordango import <app.definition.json>`.");
            return offline;
        }

        using var instance = new Instance(login.Origin, login.Token);

        var listed = await instance.ListAppsAsync(ct);
        if (!listed.Ok)
            return output.Fail($"could not read the apps on {login.Origin}", listed.Errors,
                code: ExitCodes.NoInstance);

        var apps = (listed.Payload as JsonArray ?? []).Select(RemoteApp.Of).OfType<RemoteApp>().ToList();

        if (apps.Count == 0)
            return output.Fail($"{login.Origin} has no apps this account can reach",
                ["Publish one from here first: `cordango publish`."],
                new JsonObject { ["instance"] = login.Origin, ["apps"] = new JsonArray() });

        if (args.Has("list")) return List(login.Origin, apps, output);

        var chosen = Choose(named, apps, args, output, out var refused);
        if (chosen is null) return refused;

        var fetched = await instance.GetAppAsync(chosen.Id, ct);
        if (!fetched.Ok)
            return output.Fail($"could not read {chosen.Handle} from {login.Origin}", fetched.Errors,
                code: ExitCodes.NoInstance);

        if (fetched.Body?["definition"] is not JsonObject definition)
            return output.Fail($"{chosen.Handle} has no definition on {login.Origin}",
                ["It exists but has never been given one — open it in Studio, or publish over it."]);

        output.Note($"{chosen.Name} ({chosen.Handle}) from {login.Origin}");

        return Materialize(definition, args, output, workspace,
            fallbackName: chosen.Handle,
            origin: new JsonObject
            {
                ["instance"] = login.Origin,
                ["id"] = chosen.Id,
                ["handle"] = chosen.Handle,
                ["version"] = chosen.Version,
            });
    }

    /// <summary>
    /// Which app, from what was typed or from a person reading the list.
    /// </summary>
    /// <returns>Null when the command should stop, with <paramref name="refused"/> holding the code
    /// and the reason already written.</returns>
    private static RemoteApp? Choose(string? named, List<RemoteApp> apps, Args args, Output output,
        out int refused)
    {
        refused = ExitCodes.Ok;

        if (named is { Length: > 0 })
        {
            var matches = apps.Where(a => a.Answers(named)).ToList();

            if (matches.Count == 1) return matches[0];

            refused = output.Fail(
                matches.Count == 0
                    ? $"no app called '{named}' here, and no such file either"
                    : $"'{named}' names {matches.Count} apps on this instance",
                matches.Count == 0
                    ? [$"available: {string.Join(", ", apps.Select(a => a.Handle))}"]
                    : matches.Select(m => $"{m.Handle} ({m.Id})"),
                new JsonObject { ["apps"] = new JsonArray([.. apps.Select(a => (JsonNode)a.ToJson())]) });

            return null;
        }

        if (Interview.Open(args) is not { } interview)
        {
            // No name, and nobody to ask. The list goes in the payload rather than only in the
            // message, so one call is enough for a script to find out what it should have said.
            refused = output.Fail("name the app to import",
                [$"for example: cordango import {apps[0].Handle}",
                 $"available: {string.Join(", ", apps.Select(a => a.Handle))}"],
                new JsonObject { ["apps"] = new JsonArray([.. apps.Select(a => (JsonNode)a.ToJson())]) },
                code: ExitCodes.Usage);

            return null;
        }

        var pick = interview.Choose(
            $"Which app should I import?{Environment.NewLine}"
            + Ansi.Dim($"  {apps.Count} on this instance. It comes in as source you can edit."),
            [.. apps.Select(a => (a.Handle, Describe(a)))],
            apps[0].Handle);

        return apps.FirstOrDefault(a => a.Answers(pick)) ?? apps[0];
    }

    private static string Describe(RemoteApp app)
    {
        var parts = new List<string> { app.Name };
        if (app.Entities > 0) parts.Add($"{app.Entities} entit{(app.Entities == 1 ? "y" : "ies")}");
        if (app.Status is { Length: > 0 }) parts.Add(app.Version is { Length: > 0 }
            ? $"{app.Status} · {app.Version}" : app.Status);
        return string.Join("   ", parts);
    }

    /// <summary>What is there, and nothing else. <c>--list</c> exists so that finding out costs no
    /// commitment — and so an agent can read the answer without guessing at a name first.</summary>
    private static int List(string origin, List<RemoteApp> apps, Output output) => output.Ok(
        new JsonObject
        {
            ["instance"] = origin,
            ["apps"] = new JsonArray([.. apps.Select(a => (JsonNode)a.ToJson())]),
        },
        w =>
        {
            w.WriteLine(origin);
            w.WriteLine();
            foreach (var app in apps) w.WriteLine($"  {app.Handle,-24} {Describe(app)}");
            w.WriteLine();
            w.WriteLine($"  cordango import {apps[0].Handle}");
        });

    /// <summary>The original command, unchanged: a definition on disk, and no network anywhere near
    /// it.</summary>
    private static int FromFile(string path, Args args, Output output, WorkspaceFile workspace)
    {
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

        return Materialize(document, args, output, workspace,
            fallbackName: null,
            origin: new JsonObject { ["file"] = Path.GetFullPath(path) });
    }

    /// <summary>
    /// Gate the definition, write it as source, and register it.
    ///
    /// <para>Shared by both routes on purpose: a definition fetched from an instance and the same
    /// definition saved to a file must land identically, or "import what I published" stops being a
    /// round trip and becomes two features that mostly agree.</para>
    /// </summary>
    private static int Materialize(JsonObject document, Args args, Output output,
        WorkspaceFile workspace, string? fallbackName, JsonObject origin)
    {
        // Gated BEFORE import, not after. Importing a document that was never a valid definition
        // produces a model that is wrong in ways the resulting source cannot explain, and the
        // person is then debugging generated YAML instead of the thing they actually have.
        var errors = Gate.Validate(document);
        if (errors.Count > 0)
            return output.Fail("that is not a valid App Definition", errors);

        var key = (string?)document["key"];
        if (string.IsNullOrWhiteSpace(key))
            return output.Fail("that App Definition has no key", ["an App Definition's key is its identity"]);

        var appName = args.Value("app") is { Length: > 0 } named
            ? named
            : fallbackName ?? key.Replace('_', '-');

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
            return output.Fail("that definition could not be read into the semantic model", [ex.Message]);
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
                ["from"] = origin,
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
