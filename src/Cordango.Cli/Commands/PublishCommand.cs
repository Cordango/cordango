// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Send this workspace's apps to a Cordango instance and make them live.
///
/// <para><b>It builds from source, never from <c>.cordango/</c>.</b> Publishing the last build artifact
/// would let a stale definition reach a real workspace after an edit that was never checked — the
/// artifact is a cache, and a cache is not an authority. So publish runs the same pipeline
/// <c>check</c> and <c>build</c> run and sends what it produces.</para>
///
/// <para><b>Coherent is the bar, not valid.</b> An app whose domain exists but whose screens do not
/// is a normal state mid-authoring, and refusing to publish it would make "look at it running" the
/// one thing you cannot do while building. Incomplete apps publish with the reasons printed; broken
/// ones do not publish at all.</para>
///
/// <para><b>All or nothing across the workspace.</b> Every selected app is checked BEFORE any of
/// them is sent. Publishing three apps and failing on the fourth would leave an instance holding
/// half a workspace whose cross-app references no longer resolve.</para>
/// </summary>
public static class PublishCommand
{
    public static async Task<int> RunAsync(Args args, Output output, CancellationToken ct)
    {
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        if (selection.LoadProblems is { Count: > 0 } problems)
            return output.Fail("nothing was published — the source could not be read", problems);

        var credentials = Credentials.Load();
        if (ResolveTarget(args, selection.Workspace, credentials, output, out var target) is { } targetExit)
            return targetExit;

        var reports = selection.Apps.Select(Pipeline.Check).ToList();
        if (reports.Where(r => !r.Coherent).ToList() is { Count: > 0 } broken)
        {
            return output.Fail("nothing was published — the source does not hold together",
                broken.SelectMany(r => r.Errors.Select(e => $"{r.AppKey}: {e}")),
                new JsonObject { ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]) });
        }

        var force = args.Has("force");
        using var instance = new Instance(target!.Origin, target.Token);

        var results = new JsonArray();
        var failures = new List<string>();
        var published = new List<(string Key, string Handle, bool Created, string Version)>();

        foreach (var report in reports)
        {
            var result = await instance.PublishAsync(report.Definition!, force, ct);
            if (!result.Ok)
            {
                failures.AddRange(result.Errors.Select(e => $"{report.AppKey}: {e}"));
                results.Add(new JsonObject
                {
                    ["app"] = report.AppKey,
                    ["ok"] = false,
                    ["status"] = (int)result.Status,
                    ["errors"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)e!)]),
                });

                // Stop at the first refusal rather than marching on. The most likely cause — an
                // unbumped version, a taken address — applies to the next app too, and a wall of the
                // same message is harder to act on than one.
                break;
            }

            var handle = (string?)result.Body?["handle"] ?? report.AppKey;
            var created = (bool?)result.Body?["created"] ?? false;
            var version = (string?)result.Body?["version"] ?? "";
            published.Add((report.AppKey, handle, created, version));

            results.Add(new JsonObject
            {
                ["app"] = report.AppKey,
                ["ok"] = true,
                ["handle"] = handle,
                ["version"] = version,
                ["created"] = created,
                ["url"] = $"{target.Origin}/a/{handle}",
                ["entities"] = (int?)result.Body?["entities"] ?? 0,
            });
        }

        var payload = new JsonObject
        {
            ["instance"] = target.Origin,
            ["tenantId"] = target.TenantId,
            ["apps"] = results,
        };

        if (failures.Count > 0) return output.Fail("publish failed", failures, payload);

        // Reported after a green publish, not as a failure: these apps ARE live, and an unfinished
        // one is exactly what somebody wants to open and look at.
        var unfinished = reports.Where(r => !r.Valid).Select(r => r.AppKey).ToList();

        return output.Ok(payload, w =>
        {
            w.WriteLine($"Published to {target.Origin} ({target.TenantId})");
            foreach (var (key, handle, created, version) in published)
                w.WriteLine($"  {(created ? "created" : "updated")}  {key} {version} → {target.Origin}/a/{handle}");
            if (unfinished.Count > 0)
            {
                w.WriteLine();
                w.WriteLine($"Still unfinished (published anyway): {string.Join(", ", unfinished)}");
                w.WriteLine("Run `cordango check` to see what each one is missing.");
            }
        });
    }

    /// <summary>
    /// Which instance, with which credential.
    ///
    /// <para><c>--instance</c> wins; otherwise the instance this workspace was bound to at login.
    /// Never a default and never a guess — publishing writes to somebody's real workspace, and the
    /// one thing worse than "no target" is the wrong one.</para>
    /// </summary>
    /// <returns>Null when a target was resolved; otherwise the exit code, message already written.</returns>
    private static int? ResolveTarget(Args args, WorkspaceFile workspace, Credentials credentials,
        Output output, out InstanceLogin? target)
    {
        target = null;

        var origin = args.Value("instance") ?? credentials.InstanceFor(workspace.WorkspaceId);
        if (origin is null)
        {
            return output.Fail("this workspace is not connected to an instance",
            [
                "Run `cordango login <token>` here to connect it,",
                "or name one for this command with --instance <url>.",
            ], code: ExitCodes.NoInstance);
        }

        if (credentials.Find(origin) is not { Token.Length: > 0 } login)
        {
            return output.Fail($"not signed in to {origin}",
                [$"Run `cordango login <token> --instance {origin}`."], code: ExitCodes.NoInstance);
        }

        target = login;
        return null;
    }
}
