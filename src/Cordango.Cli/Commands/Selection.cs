// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// The opening move of every command that reads a workspace: find the root, load the registered
/// apps, and narrow to <c>--app</c> if one was named.
///
/// <para>Shared because the failures are shared. "No workspace here" and "no app called X" have to
/// read identically from <c>check</c>, <c>build</c>, <c>inspect</c> and <c>apply</c>, or an agent
/// learns four spellings of the same problem and handles three of them.</para>
/// </summary>
public sealed record Selection(WorkspaceFile Workspace, IReadOnlyList<LoadedApp> Apps)
{
    /// <returns>Null when the command should stop; <paramref name="exit"/> then holds the code and
    /// the message has already been written.</returns>
    public static Selection? Resolve(Args args, Output output, out int exit)
    {
        exit = ExitCodes.Ok;

        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
        {
            exit = output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? ["Run `cordango new <app-name>` in an empty directory to create one."] : [],
                code: ExitCodes.NoWorkspace);
            return null;
        }

        var loaded = workspace.Apps.Select(path => AppFolder.Load(workspace.Root, path)).ToList();

        if (args.Value("app") is not { Length: > 0 } wanted)
            return new Selection(workspace, loaded);

        // Matched on the app KEY first, then the directory name — the key is authoritative, but
        // somebody typing the folder they are standing in should not get "no such app".
        var narrowed = loaded
            .Where(a => string.Equals(a.Key, wanted, StringComparison.Ordinal)
                     || string.Equals(Path.GetFileName(a.Path), wanted, StringComparison.Ordinal))
            .ToList();

        if (narrowed.Count == 0)
        {
            exit = output.Fail($"no app '{wanted}' in this workspace",
                [$"registered: {(loaded.Count == 0 ? "(none)" : string.Join(", ", loaded.Select(a => a.Key)))}"]);
            return null;
        }

        return new Selection(workspace, narrowed);
    }

    /// <summary>Apps whose files could not be assembled at all. Every command reports these before
    /// doing its own work — there is no sense checking an app that did not load.</summary>
    public IReadOnlyList<string> LoadProblems => [.. Apps.SelectMany(a => a.Problems)];

    public bool AllLoaded => Apps.All(a => a.Ok);
}
