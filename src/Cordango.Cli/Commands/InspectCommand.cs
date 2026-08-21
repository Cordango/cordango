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
/// Describe the workspace, one app, or one aggregate inside it.
///
/// <para><b>The point is what it does NOT return.</b> CordyOSS §8.3: an agent should receive a
/// concise description of the aggregates that bear on its task, never every source file in the
/// repository and never the App Definition schema. <c>cordango inspect entities/claim</c> is how a turn
/// stays small on the fourth round of revisions, which is the whole reason the semantic layer exists.
/// </para>
/// </summary>
public static class InspectCommand
{
    public static int Run(Args args, Output output)
    {
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        var path = args.First;

        // No app named and no path: the workspace outline. The one call an agent makes first, and it
        // has to be cheap enough that making it first is obviously right.
        if (args.Value("app") is null && path is null)
            return Outline(selection, output);

        if (selection.Apps.Count != 1)
            return output.Usage("--app is required to inspect inside an app "
                + $"({string.Join(", ", selection.Apps.Select(a => a.Key))})");

        var loaded = selection.Apps[0];
        if (loaded.App is null)
            return output.Fail($"{loaded.Key} could not be read", loaded.Problems);

        var description = CordInspect.Describe(loaded.App, path);

        // Unlike apply and fmt, inspect still answers for an app with load problems — it is the
        // command somebody reaches for to work out WHY it is broken, and refusing to describe it
        // would send them back to reading files by hand. The problems ride along so the answer is
        // never mistaken for a description of the whole app.
        return output.Ok(
            new JsonObject
            {
                ["app"] = loaded.Key,
                ["path"] = path,
                ["description"] = description,
                ["problems"] = new JsonArray([.. loaded.Problems.Select(p => (JsonNode)p!)]),
            },
            w =>
            {
                w.WriteLine(description);
                foreach (var problem in loaded.Problems) output.Note("problem: " + problem);
            });
    }

    private static int Outline(Selection selection, Output output)
    {
        var apps = selection.Apps.Select(a => new JsonObject
        {
            ["key"] = a.Key,
            ["path"] = a.Path,
            ["name"] = a.App?.Name,
            ["entities"] = a.App?.EntityList.Count ?? 0,
            ["screens"] = a.App?.Screens?.Count ?? 0,
            ["readable"] = a.Ok,
        }).ToList();

        return output.Ok(
            new JsonObject
            {
                ["workspace"] = selection.Workspace.Name,
                ["root"] = selection.Workspace.Root,
                ["apps"] = new JsonArray([.. apps.Select(a => (JsonNode)a)]),
                ["coreApps"] = CoreApps(),
            },
            w =>
            {
                w.WriteLine($"{selection.Workspace.Name} ({selection.Apps.Count} app"
                    + $"{(selection.Apps.Count == 1 ? "" : "s")})");

                foreach (var app in selection.Apps)
                {
                    if (app.App is null)
                    {
                        w.WriteLine($"  {app.Key,-24} unreadable");
                        continue;
                    }

                    w.WriteLine($"  {app.Key,-24} {app.App.EntityList.Count} entities, "
                        + $"{app.App.Screens?.Count ?? 0} screens   {app.Path}");
                }

                w.WriteLine();
                w.WriteLine("Core apps — the platform provides these to every workspace. LINK to them,");
                w.WriteLine("never re-declare them: type: reference, targetApp: <key>, target: <entity>.");
                foreach (var core in CoreAppRegistry.All)
                    w.WriteLine($"  {core.SystemKey,-24} {core.Name,-16} "
                        + string.Join(", ", core.Entities.Select(e => e.Key)));

                w.WriteLine();
                w.WriteLine("  cordango vocabulary core organizations   what one core app holds");
            });
    }

    /// <summary>
    /// The core apps, in the ONE call an agent is told to make first.
    ///
    /// <para><b>This is the whole fix for a real failure.</b> Asked to link its tasks to organizations,
    /// an agent found no way to discover that Organizations already exists, reasonably concluded the
    /// concept was unmodelled, and declared a second one. The gate would have accepted the correct
    /// reference all along. Nothing it could run would tell it so.</para>
    ///
    /// <para>Entity KEYS are listed rather than counted because the key is the thing a reference has to
    /// name, and it does not always match the label — Organizations declares <c>organization</c> and
    /// calls it "Company". Static registry data, so this costs no network and no database and works in
    /// a workspace that has never been logged in.</para>
    /// </summary>
    private static JsonArray CoreApps() =>
        new([.. CoreAppRegistry.All.Select(c => (JsonNode)new JsonObject
        {
            ["systemKey"] = c.SystemKey,
            ["name"] = c.Name,
            ["entities"] = new JsonArray([.. c.Entities.Select(e => (JsonNode)e.Key)]),
        })]);
}
