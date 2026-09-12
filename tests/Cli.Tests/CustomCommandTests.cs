// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango

namespace Cordango.Cli.Tests;

/// <summary>
/// Setting up the place custom code lives, and keeping the types an editor reads in step with the
/// definition.
/// </summary>
public sealed class CustomCommandTests
{
    private static string Custom(Sandbox cord, params string[] parts) =>
        cord.Path_(["apps", "support", "custom", "dotnet", .. parts]);

    [Fact]
    public void It_writes_a_project_a_sample_and_a_gitignore()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        Assert.Equal(ExitCodes.Ok, cord.Run("custom"));

        Assert.True(File.Exists(Custom(cord, "Support.Custom.csproj")));
        Assert.True(File.Exists(Custom(cord, "Rules.cs")));
        Assert.True(File.Exists(Custom(cord, ".gitignore")));
    }

    [Fact]
    public void The_editor_project_references_no_generated_project()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        var project = File.ReadAllText(Custom(cord, "Support.Custom.csproj"));

        // A ProjectReference to the generated app would define every entity twice: it compiles a
        // copy of these same sources.
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
        Assert.Contains("Cordango.Standalone", project, StringComparison.Ordinal);
        Assert.Contains("Cordango.Standalone.Custom", project, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sample_it_writes_is_something_the_scanner_accepts()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        Assert.Equal(ExitCodes.Ok, cord.Run("check"));
    }

    [Fact]
    public void It_never_overwrites_code_somebody_has_written()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        File.WriteAllText(Custom(cord, "Rules.cs"), "// mine\n");
        cord.Run("custom");

        Assert.Equal("// mine\n", File.ReadAllText(Custom(cord, "Rules.cs")));
    }

    [Fact]
    public void The_record_types_land_where_an_editor_will_look()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        cord.Run("custom");

        var entities = Custom(cord, ".generated", "Entities");
        Assert.True(Directory.Exists(entities));
        Assert.NotEmpty(Directory.EnumerateFiles(entities, "*.cs"));
    }

    [Fact]
    public void A_record_that_leaves_the_definition_loses_its_copy()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        var stale = Custom(cord, ".generated", "Entities", "Gone.cs");
        File.WriteAllText(stale, "// an entity that is no longer in the definition\n");

        cord.Run("custom");

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void Building_refreshes_the_types_for_an_app_that_has_custom_code()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        var stale = Custom(cord, ".generated", "Entities", "Gone.cs");
        File.WriteAllText(stale, "// stale\n");

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("custom");

        var stale = Custom(cord, ".generated", "Entities", "Gone.cs");
        File.WriteAllText(stale, "// stale\n");

        cord.Run("build", "--dry-run");

        Assert.True(File.Exists(stale), "--dry-run promises to write nothing, and that includes this");
    }

    [Fact]
    public void An_app_with_no_custom_directory_is_left_alone_by_the_build()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.False(Directory.Exists(Custom(cord, ".generated")));
    }
}
