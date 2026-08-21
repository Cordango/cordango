// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cli.Tests;

/// <summary>
/// A workspace written before the command was renamed still opens, and stops being one the first
/// time anything writes.
///
/// <para>The rename touched three names at once — <c>cord.yaml</c>, <c>*.cord.yaml</c> and
/// <c>.cord/</c> — and the failure mode of getting it half right is quiet in both directions. Read
/// support without migration leaves people on the old names indefinitely while every document says
/// otherwise. Migration without care leaves BOTH files on disk, and then the next load picks
/// whichever the filesystem happened to enumerate last, which is not a bug anyone would think to
/// look for.</para>
/// </summary>
public class LegacyNameTests
{
    /// <summary>Builds a current workspace, then renames every file back to what it was called
    /// before, so the fixture is a real pre-rename checkout rather than a hand-written guess at
    /// one.</summary>
    private static void Rewind(Sandbox sandbox)
    {
        File.Move(sandbox.Path_("cordango.yaml"), sandbox.Path_("cord.yaml"));

        foreach (var file in Directory.EnumerateFiles(sandbox.Root, "*.cordango.yaml",
                     SearchOption.AllDirectories).ToList())
        {
            File.Move(file, file[..^".cordango.yaml".Length] + ".cord.yaml");
        }
    }

    [Fact]
    public void A_workspace_written_before_the_rename_still_checks()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(ExitCodes.Ok, sandbox.Run("new", "claims"));
        Rewind(sandbox);

        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
    }

    [Fact]
    public void Formatting_migrates_the_old_names_and_leaves_none_behind()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        Rewind(sandbox);

        Assert.Equal(ExitCodes.Ok, sandbox.Run("fmt"));

        Assert.True(File.Exists(sandbox.Path_("cordango.yaml")));
        Assert.False(File.Exists(sandbox.Path_("cord.yaml")));

        // Not "the new files exist" — that would pass with both spellings on disk, which is the
        // outcome this is here to rule out.
        Assert.Empty(Directory.EnumerateFiles(sandbox.Root, "*.cord.yaml", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateFiles(sandbox.Root, "*.cordango.yaml", SearchOption.AllDirectories));
    }

    [Fact]
    public void The_migrated_workspace_still_checks()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        Rewind(sandbox);
        sandbox.Run("fmt");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
    }

    /// <summary>
    /// The content is byte-identical either way, so nothing but the filename has changed — and that
    /// is exactly the case a content-comparing formatter reports as "already canonical" and walks
    /// away from. It did, before this test existed.
    /// </summary>
    [Fact]
    public void Check_only_formatting_reports_the_old_names_as_drift()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        Rewind(sandbox);

        Assert.Equal(ExitCodes.Failed, sandbox.Run("fmt", "--check"));
        Assert.Contains("cordango.yaml", sandbox.Error);

        // --check changes nothing, so the workspace is still on the old names afterwards.
        Assert.True(File.Exists(sandbox.Path_("cord.yaml")));
    }

    [Fact]
    public void Build_output_goes_to_the_current_directory_name()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        Rewind(sandbox);

        Assert.Equal(ExitCodes.Ok, sandbox.Run("build"));

        Assert.True(File.Exists(sandbox.Path_(".cordango", "build", "claims", "app.definition.json")));
        Assert.False(Directory.Exists(sandbox.Path_(".cord")));
    }
}
