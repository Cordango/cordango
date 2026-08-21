// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Drop a stored credential.
///
/// <para><b>Local only.</b> This forgets the token on this machine; it does not revoke it. A key
/// that may have leaked has to be revoked where it was minted — under the avatar menu — and saying
/// so here is the difference between somebody believing they are safe and being safe.</para>
/// </summary>
public static class LogoutCommand
{
    public static int Run(Args args, Output output)
    {
        var credentials = Credentials.Load();

        // With no argument: this workspace's instance if we are in one, otherwise nothing —
        // silently signing out of every instance because the user was in the wrong directory is a
        // surprise nobody wants twice. `--all` is the explicit spelling.
        string? origin = null;
        if (!args.Has("all"))
        {
            origin = args.Value("instance") ?? args.First;
            if (origin is null)
            {
                var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out _);
                origin = workspace is { WorkspaceId.Length: > 0 }
                    ? credentials.InstanceFor(workspace.WorkspaceId)
                    : null;
            }

            if (origin is null)
                return output.Fail("no instance to sign out of",
                [
                    "Run this inside a workspace that has been logged in, name one with --instance <url>,",
                    "or use --all to forget every stored credential.",
                ]);
        }

        var dropped = credentials.Forget(origin);
        if (dropped == 0)
            return output.Fail($"not signed in to {origin}", [$"stored: {Stored(credentials)}"]);

        credentials.Flush();

        return output.Ok(
            new JsonObject { ["forgotten"] = dropped, ["instance"] = origin },
            w =>
            {
                w.WriteLine(origin is null
                    ? $"Forgot {dropped} stored credential{(dropped == 1 ? "" : "s")}."
                    : $"Signed out of {origin}.");
                w.WriteLine("The token itself is still valid — revoke it under Personal Access Keys.");
            });
    }

    private static string Stored(Credentials credentials) =>
        credentials.Origins.ToList() is { Count: > 0 } origins ? string.Join(", ", origins) : "(none)";
}
