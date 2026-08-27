// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cli.Workspace;

namespace Cordango.Cli.Remote;

/// <summary>
/// Which instance this workspace answers to, with which credential.
///
/// <para><b>One implementation, because the refusal has to be one sentence.</b> Three commands need
/// this answer — <c>publish</c> before it sends anything, <c>configure</c> before it will write
/// <c>target: platform</c> into the file, and <c>build</c> before it will act on that line — and
/// "you are not connected" said three ways is three things to learn instead of one.</para>
///
/// <para><b>It reads the stored credential and does not call anybody.</b> Whether the key is still
/// valid is a question only the instance can answer and only <c>cordango whoami</c> asks it; what
/// this decides is whether there is a credential to try at all. That distinction is what keeps
/// <c>build</c> offline even when it is building for the platform.</para>
/// </summary>
public static class Connection
{
    /// <summary>
    /// The same answer, without saying anything when there is none.
    ///
    /// <para>For the one caller that has a better message than this class does: <c>import</c> given
    /// an argument that is not a file, where "you are not connected" is the wrong sentence and "no
    /// such file" is the right one.</para>
    /// </summary>
    public static InstanceLogin? Find(Args args, WorkspaceFile workspace)
    {
        var credentials = Credentials.Load();
        var origin = args.Value("instance") ?? credentials.InstanceFor(workspace.WorkspaceId);
        return origin is null ? null : credentials.Find(origin) is { Token.Length: > 0 } login ? login : null;
    }

    /// <returns>The stored login, or null when the command should stop; <paramref name="exit"/> then
    /// holds the code and the message has already been written.</returns>
    public static InstanceLogin? Resolve(Args args, WorkspaceFile workspace, Output output, out int exit)
    {
        exit = ExitCodes.Ok;

        var credentials = Credentials.Load();

        // `--instance` wins, because somebody typing it has a reason — a tunnel, a hostname the
        // server does not know it answers on. Otherwise the instance this workspace was bound to at
        // login. Never a default and never a guess.
        var origin = args.Value("instance") ?? credentials.InstanceFor(workspace.WorkspaceId);
        if (origin is null)
        {
            exit = output.Fail("this workspace is not connected to an instance",
            [
                "Run `cordango login <token>` here to connect it,",
                "or name one for this command with --instance <url>.",
            ], code: ExitCodes.NoInstance);
            return null;
        }

        if (credentials.Find(origin) is not { Token.Length: > 0 } login)
        {
            exit = output.Fail($"not signed in to {origin}",
                [$"Run `cordango login <token> --instance {origin}`."], code: ExitCodes.NoInstance);
            return null;
        }

        return login;
    }
}
