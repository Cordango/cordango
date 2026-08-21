// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;
using Cordango.Cord;

namespace Cordango.Cli.Commands;

/// <summary>
/// Rewrite every <c>.cordango</c> file in canonical form.
///
/// <para><b>Canonical formatting is contract, not tidiness.</b> Two paths that produce the same
/// application must produce the same bytes — otherwise every diff is whitespace noise and "the files
/// and the model agree" stops being a checkable claim. A hand edit that reorders keys or indents with
/// four spaces is corrected here rather than argued about in review.</para>
///
/// <para>Loading already goes through the model, so this cannot reformat a file it does not
/// understand: <c>fmt</c> on unreadable source reports the same problems <c>check</c> would.</para>
/// </summary>
public static class FmtCommand
{
    public static int Run(Args args, Output output)
    {
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        // `!Ok` rather than `App is null`: formatting an app whose files were partly refused would
        // rewrite the tree from a model missing what was dropped, deleting source to tidy it.
        var unreadable = selection.Apps.Where(a => !a.Ok).ToList();
        if (unreadable.Count > 0)
            return output.Fail("nothing was formatted — the source could not be read",
                unreadable.SelectMany(a => a.Problems));

        // `--check` is the CI shape: report drift, change nothing. A separate flag rather than the
        // default, because a formatter that silently edits during a build is a formatter that makes
        // CI green on source nobody has seen.
        var checkOnly = args.Has("check");

        var diffs = new List<FileDiff>();
        foreach (var loaded in selection.Apps)
        {
            var source = CordSource.Write(loaded.App!);
            diffs.Add(checkOnly ? AppFolder.Diff(loaded, source) : AppFolder.Save(loaded, source));
        }

        var written = diffs.SelectMany(d => d.Written).ToList();
        var deleted = diffs.SelectMany(d => d.Deleted).ToList();

        // The root manifest is part of canonical form too, and it is the one file no app-level diff
        // can reach. A workspace created before the rename would otherwise keep its old manifest
        // forever while every app file around it moved on.
        var legacyManifest = System.IO.Path.Combine(
            selection.Workspace.Root, WorkspaceFile.LegacyFileName);
        if (File.Exists(legacyManifest))
        {
            written.Add(WorkspaceFile.FileName);
            deleted.Add(WorkspaceFile.LegacyFileName);
            if (!checkOnly) selection.Workspace.Save();   // writes the new name, removes the old
        }

        if (checkOnly && (written.Count > 0 || deleted.Count > 0))
            return output.Fail("these files are not canonically formatted", written.Concat(deleted));

        return output.Ok(
            new JsonObject
            {
                ["checkOnly"] = checkOnly,
                ["written"] = new JsonArray([.. written.Select(p => (JsonNode)p!)]),
                ["deleted"] = new JsonArray([.. deleted.Select(p => (JsonNode)p!)]),
            },
            w =>
            {
                foreach (var path in written) w.WriteLine("  formatted " + path);
                foreach (var path in deleted) w.WriteLine("  deleted   " + path);
                if (written.Count == 0 && deleted.Count == 0) w.WriteLine("already canonical");
            });
    }
}
