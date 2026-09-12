// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango

namespace Cordango.Cli.Tests;

/// <summary>
/// What custom code becomes in the generated application: a copy under <c>api/Custom/</c>, and a
/// call to it inside the computed field that named it.
/// </summary>
public sealed class CustomCodeBuildTests
{
    private const string Pricing = """
        namespace Support.Custom;

        [CordangoFunctions]
        public static class Pricing
        {
            [CordangoFunction("surcharge")]
            public static decimal? Surcharge(decimal? total) => total;
        }
        """;

    private static void WriteCustom(Sandbox cord, string content)
    {
        var directory = cord.Path_("apps", "support", "custom", "dotnet");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Pricing.cs"), content);
    }

    private static string Generated(Sandbox cord, string relative) =>
        File.ReadAllText(cord.Path_(["generated", "support", .. relative.Split('/')]));

    private static bool Exists(Sandbox cord, string relative) =>
        File.Exists(cord.Path_(["generated", "support", .. relative.Split('/')]));

    [Fact]
    public void The_source_is_copied_in_under_a_banner_that_names_the_original()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("configure", "--target", "standalone");
        WriteCustom(cord, Pricing);

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.True(Exists(cord, "api/Custom/Pricing.cs"));

        var copied = Generated(cord, "api/Custom/Pricing.cs");
        Assert.Contains("Generated from custom/dotnet/Pricing.cs", copied, StringComparison.Ordinal);
        Assert.Contains("replaced every time", copied, StringComparison.Ordinal);
        Assert.Contains("public static decimal? Surcharge", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_with_no_custom_code_gets_no_custom_directory()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("configure", "--target", "standalone");

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.False(Exists(cord, "api/Custom/Pricing.cs"));
    }

    [Fact]
    public void The_build_is_byte_identical_when_nothing_changed()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("configure", "--target", "standalone");
        WriteCustom(cord, Pricing);

        cord.Run("build");
        var first = Generated(cord, "api/Custom/Pricing.cs");

        cord.Run("build");
        var second = Generated(cord, "api/Custom/Pricing.cs");

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_removed_source_is_removed_from_the_application()
    {
        using var cord = new Sandbox();
        cord.Run("new", "support");
        cord.Run("configure", "--target", "standalone");
        WriteCustom(cord, Pricing);
        cord.Run("build");
        Assert.True(Exists(cord, "api/Custom/Pricing.cs"));

        Directory.Delete(cord.Path_("apps", "support", "custom"), recursive: true);

        Assert.Equal(ExitCodes.Ok, cord.Run("build"));
        Assert.False(Exists(cord, "api/Custom/Pricing.cs"));
    }
}
