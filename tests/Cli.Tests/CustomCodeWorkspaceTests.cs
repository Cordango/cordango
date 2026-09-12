// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

/// <summary>
/// Custom code, end to end through the real CLI: a directory of C# beside the YAML becomes a
/// contract inside the compiled definition, and the definition's hash moves when the code does.
/// </summary>
public sealed class CustomCodeWorkspaceTests
{
    private const string Pricing = """
        namespace Support.Custom;

        [CordangoFunctions]
        public static class Pricing
        {
            [CordangoFunction("discount", Description = "Tier discount.")]
            public static decimal? Discount(decimal? total, string? tier) =>
                tier == "gold" ? total : total;
        }
        """;

    private static void WriteCustom(Sandbox cord, string content, string file = "Pricing.cs")
    {
        var directory = cord.Path_("apps", "support", "custom", "dotnet");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), content);
    }

    private static JsonObject? Definition(Sandbox cord)
    {
        var path = cord.Path_(".cordango", "build", "support", "app.definition.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() : null;
    }

    [Fact]
    public void A_directory_of_csharp_becomes_a_contract_in_the_definition()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        WriteCustom(cord, Pricing);

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));

        var custom = Definition(cord)?["custom"]?.AsObject();
        Assert.NotNull(custom);
        Assert.Equal("dotnet", (string?)custom["language"]);
        Assert.StartsWith("sha256:", (string?)custom["hash"], StringComparison.Ordinal);

        var fn = custom["functions"]!.AsArray().Single()!.AsObject();
        Assert.Equal("discount", (string?)fn["name"]);
        Assert.Equal("number", (string?)fn["returns"]);
        Assert.Equal("Pricing.cs", (string?)fn["source"]!["file"]);
    }

    [Fact]
    public void An_app_with_no_custom_directory_has_no_custom_section()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.Null(Definition(cord)?["custom"]);
    }

    [Fact]
    public void Editing_a_body_changes_the_definition_hash()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        WriteCustom(cord, Pricing);
        cord.Run("build");
        var before = (string?)Definition(cord)!["custom"]!["hash"];

        WriteCustom(cord, Pricing.Replace("total : total", "total : total * 2", StringComparison.Ordinal)
            + "\n// a change with no new signature\n");
        cord.Run("build");
        var after = (string?)Definition(cord)!["custom"]!["hash"];

        Assert.NotNull(before);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Code_that_does_not_hold_together_fails_the_check()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        WriteCustom(cord, """
            namespace Support.Custom;

            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("discount")]
                public static decimal? Discount(int total) => total;
            }
            """);

        Assert.NotEqual(ExitCodes.Ok, cord.Run("check"));
        Assert.Contains("decimal?, bool? or string?", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authored_custom_section_is_refused_rather_than_overwritten()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        var appFile = cord.Path_("apps", "support", "app.cordango.yaml");
        File.AppendAllText(appFile,
            "\ncustom:\n  language: dotnet\n  hash: \"sha256:"
            + new string('a', 64) + "\"\n");

        Assert.NotEqual(ExitCodes.Ok, cord.Run("check"));
        Assert.Contains("derived from the code under custom/", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_says_when_the_built_application_is_older_than_the_code()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        WriteCustom(cord, Pricing);
        cord.Run("build");

        Assert.Equal(ExitCodes.Ok, cord.Run("doctor"));

        WriteCustom(cord, Pricing + "// changed after the build");

        Assert.NotEqual(ExitCodes.Ok, cord.Run("doctor"));
        Assert.Contains("changed since the last build", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_refuses_an_application_that_carries_code()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        WriteCustom(cord, Pricing);

        var exit = cord.Run("check", "--target", "platform");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains("cannot run on platform", cord.Error, StringComparison.Ordinal);
        Assert.Contains("no compiler in the runtime", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Building_for_the_platform_refuses_it_too()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance();
        cord.Run("new", "support");

        // A connection first: `build --target platform` checks that BEFORE it compiles anything,
        // deliberately, so that "you are not connected" never costs a full compile to learn.
        var workspace = WorkspaceFile.Find(cord.Root, out _)!;
        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(instance.Origin, "cord_pat.a.b.c", "default",
            "t@example.com", DateTimeOffset.UtcNow));
        credentials.Bind(workspace.WorkspaceId, instance.Origin);
        credentials.Flush();

        WriteCustom(cord, Pricing);

        Assert.NotEqual(ExitCodes.Ok, cord.Run("build", "--target", "platform"));
        Assert.Contains("cannot run on the platform", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_with_no_code_still_checks_against_the_platform()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        Assert.Equal(ExitCodes.Ok, cord.Run("check", "--target", "platform"));
    }

    [Fact]
    public void A_language_no_target_reads_stops_the_check()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");

        var directory = cord.Path_("apps", "support", "custom", "python");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pricing.py"), "# nope\n");

        Assert.NotEqual(ExitCodes.Ok, cord.Run("check"));
        Assert.Contains("nothing here reads", cord.Error, StringComparison.Ordinal);
    }
}
