// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cli.Tests;

public class LegacyNameTests
{
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

    [Fact]
    public void Check_only_formatting_reports_the_old_names_as_drift()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        Rewind(sandbox);

        Assert.Equal(ExitCodes.Failed, sandbox.Run("fmt", "--check"));
        Assert.Contains("cordango.yaml", sandbox.Error);

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
