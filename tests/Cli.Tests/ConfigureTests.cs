// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

public sealed class ConfigureTests
{
    private const string Origin = "http://localhost:5215";

    [Fact]
    public void A_new_workspace_is_not_configured_and_still_builds()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Null(WorkspaceFile.Find(cord.Root, out _)!.Build);

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.Contains("no build configuration", cord.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(cord.Path_(".cordango", "build", "claims", "app.definition.json")));
    }

    [Fact]
    public void Configuring_a_target_makes_build_generate_the_whole_application()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, cord.Run("configure", "--target", "standalone"));
        Assert.Equal(ExitCodes.Ok, cord.Run("build"));

        Assert.True(File.Exists(cord.Path_("generated", "claims", "docker-compose.yml")));
        Assert.True(File.Exists(cord.Path_("generated", "claims", "cordango.build.json")));
    }

    [Fact]
    public void Every_app_gets_its_own_directory_under_the_configured_one()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        cord.Run("add", "app", "orders");

        Assert.Equal(ExitCodes.Ok, cord.Run("configure", "--target", "standalone"));
        Assert.Equal(ExitCodes.Ok, cord.Run("build"));

        Assert.True(File.Exists(cord.Path_("generated", "claims", "docker-compose.yml")));
        Assert.True(File.Exists(cord.Path_("generated", "orders", "docker-compose.yml")));
    }

    [Fact]
    public void The_retired_out_flag_is_refused_rather_than_ignored()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.Usage, cord.Run("build", "--target", "standalone", "--out", "../elsewhere"));
        Assert.Contains("--out is gone", cord.Error, StringComparison.Ordinal);
        Assert.Contains("generated/", cord.Error, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Usage, cord.Run("configure", "--target", "standalone", "--out", "dist"));
        Assert.False(Directory.Exists(cord.Path_("generated")));
    }

    [Fact]
    public void The_configuration_survives_every_other_command_that_rewrites_the_manifest()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        cord.Run("configure", "--target", "standalone", "--runtime", "source");

        Assert.Equal(ExitCodes.Ok, cord.Run("add", "app", "orders"));

        var reloaded = WorkspaceFile.Find(cord.Root, out _)!;
        Assert.Equal(BuildConfig.RuntimeSource, reloaded.Build!.Runtime);
        Assert.Equal(2, reloaded.Apps.Count);
    }

    [Fact]
    public void The_interview_takes_the_number_as_readily_as_the_name()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        Connect(cord);

        Assert.Equal(ExitCodes.Ok, cord.RunAnswering("2\n", "configure"));

        var written = WorkspaceFile.Find(cord.Root, out _)!.Build;
        Assert.Equal(BuildConfig.Platform, written!.Target);
    }

    [Fact]
    public void The_interview_refuses_the_platform_it_cannot_reach()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.NoInstance, cord.RunAnswering("platform\n", "configure"));
        Assert.Null(WorkspaceFile.Find(cord.Root, out _)!.Build);
    }

    [Fact]
    public void Pressing_enter_takes_the_defaults()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, cord.RunAnswering("\n", "configure"));

        var written = WorkspaceFile.Find(cord.Root, out _)!.Build;
        Assert.Equal(BuildConfig.Standalone, written!.Target);
        Assert.Equal(BuildConfig.RuntimePackage, written.Runtime);
        Assert.False(written.AllowIncomplete);
    }

    [Fact]
    public void A_first_build_asks_and_then_does_what_it_was_told()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, cord.RunAnswering("standalone\n", "build"));

        Assert.NotNull(WorkspaceFile.Find(cord.Root, out _)!.Build);
        Assert.True(File.Exists(cord.Path_("generated", "claims", "docker-compose.yml")));
    }

    [Fact]
    public void A_command_that_asked_for_json_is_never_asked_a_question()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Interview.Scripted = new StringReader("standalone\n");
        try
        {
            var (exit, payload) = cord.RunJson("build");
            Assert.Equal(ExitCodes.Ok, exit);
            Assert.True((bool)payload["ok"]!);
        }
        finally
        {
            Interview.Scripted = null;
        }

        Assert.Null(WorkspaceFile.Find(cord.Root, out _)!.Build);
    }

    [Fact]
    public void An_explicit_target_answers_the_question_rather_than_provoking_it()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok,
            cord.RunAnswering("platform\n", "build", "--target", "standalone"));

        Assert.Null(WorkspaceFile.Find(cord.Root, out _)!.Build);
        Assert.True(File.Exists(cord.Path_("generated", "claims", "docker-compose.yml")));
    }

    [Fact]
    public void The_platform_target_is_refused_until_the_workspace_is_connected()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var exit = cord.Run("configure", "--target", "platform");

        Assert.Equal(ExitCodes.NoInstance, exit);
        Assert.Contains("not connected to an instance", cord.Error, StringComparison.Ordinal);
        Assert.Contains("cordango login", cord.Error, StringComparison.Ordinal);
        Assert.Null(WorkspaceFile.Find(cord.Root, out _)!.Build);
    }

    [Fact]
    public void The_platform_target_is_accepted_once_it_is()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        Connect(cord);

        Assert.Equal(ExitCodes.Ok, cord.Run("configure", "--target", "platform"));

        var written = WorkspaceFile.Find(cord.Root, out _)!.Build;
        Assert.True(written!.IsPlatform);
    }

    [Fact]
    public void Building_a_platform_workspace_that_lost_its_credential_refuses()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        Configure(cord, "target: platform");

        var exit = cord.Run("build");

        Assert.Equal(ExitCodes.NoInstance, exit);
        Assert.Contains("not connected to an instance", cord.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cord.Path_(".cordango", "build", "claims")));
    }

    [Fact]
    public void Building_a_connected_platform_workspace_compiles_and_stops_short_of_publishing()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        Connect(cord);
        Configure(cord, "target: platform");

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));

        Assert.True(File.Exists(cord.Path_(".cordango", "build", "claims", "app.definition.json")));
        Assert.Contains("cordango publish", cord.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_the_file_names_but_this_build_does_not_have_is_reported_as_such()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        Configure(cord, "target: sideways");

        var exit = cord.Run("build");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("sideways", cord.Error, StringComparison.Ordinal);
        Assert.Contains("standalone", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_treats_the_platform_as_a_target_that_withholds_nothing()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var (exit, payload) = cord.RunJson("check", "--target", "platform");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal("platform", (string?)payload["target"]);
    }

    [Fact]
    public void The_banner_is_for_people_and_never_reaches_a_json_caller()
    {
        using var cord = new Sandbox();

        Assert.Equal(ExitCodes.Ok, cord.Run("version"));
        Assert.Contains("████", cord.Out, StringComparison.Ordinal);

        var (_, payload) = cord.RunJson("version");
        Assert.DoesNotContain("████", cord.Out, StringComparison.Ordinal);
        Assert.NotNull((string?)payload["cordango"]);
    }

    [Fact]
    public void The_generated_instructions_send_an_agent_to_ask_rather_than_to_model()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var instructions = File.ReadAllText(cord.Path_("CLAUDE.md"));

        Assert.Contains("Start with questions", instructions, StringComparison.Ordinal);
        Assert.Contains("What is the record?", instructions, StringComparison.Ordinal);
        Assert.Contains("cordango configure", instructions, StringComparison.Ordinal);
        Assert.Equal(instructions, File.ReadAllText(cord.Path_("AGENTS.md")));
    }

    private static void Connect(Sandbox cord)
    {
        var workspace = WorkspaceFile.Find(cord.Root, out _)!;
        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(Origin, "cord_pat.a.b.c", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Bind(workspace.WorkspaceId, Origin);
        credentials.Flush();
    }

    private static void Configure(Sandbox cord, string line)
    {
        var path = cord.Path_(WorkspaceFile.FileName);
        File.WriteAllText(path,
            File.ReadAllText(path).Replace("apps:", $"build:\n  {line}\napps:", StringComparison.Ordinal));
    }
}
