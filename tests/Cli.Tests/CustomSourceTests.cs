// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango

using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

/// <summary>
/// Reading <c>custom/&lt;language&gt;/</c> off the disk: what counts as a source, what the hash
/// covers, and what is refused before anything is compiled.
/// </summary>
public sealed class CustomSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cordango-custom-" + Guid.NewGuid().ToString("N")[..8]);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void An_app_with_no_custom_directory_carries_nothing()
    {
        Directory.CreateDirectory(_root);

        var read = CustomSourceLoader.Read(_root);

        Assert.Null(read.Bundle);
        Assert.Empty(read.Problems);
    }

    [Fact]
    public void Sources_are_found_recursively_and_keyed_by_forward_slashed_path()
    {
        Write("custom/dotnet/Pricing.cs", "// a\n");
        Write("custom/dotnet/Hooks/Invoices.cs", "// b\n");

        var bundle = CustomSourceLoader.Read(_root).Bundle;

        Assert.NotNull(bundle);
        Assert.Equal("dotnet", bundle.Language);
        Assert.Equal(["Hooks/Invoices.cs", "Pricing.cs"], bundle.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_editor_copies_and_build_output_are_not_custom_code()
    {
        Write("custom/dotnet/Pricing.cs", "// a\n");
        Write("custom/dotnet/.generated/Entities/Invoice.cs", "// generated\n");
        Write("custom/dotnet/obj/Debug/AssemblyInfo.cs", "// obj\n");
        Write("custom/dotnet/bin/Debug/Thing.cs", "// bin\n");

        var bundle = CustomSourceLoader.Read(_root).Bundle;

        Assert.NotNull(bundle);
        Assert.Equal(["Pricing.cs"], bundle.Files.Keys);
    }

    [Fact]
    public void Only_the_language_extension_is_read()
    {
        Write("custom/dotnet/Pricing.cs", "// a\n");
        Write("custom/dotnet/notes.md", "not code\n");
        Write("custom/dotnet/Pricing.csproj", "<Project />\n");

        var bundle = CustomSourceLoader.Read(_root).Bundle;

        Assert.NotNull(bundle);
        Assert.Equal(["Pricing.cs"], bundle.Files.Keys);
    }

    [Fact]
    public void Content_is_normalised_so_a_checkout_cannot_change_the_hash()
    {
        Write("custom/dotnet/Pricing.cs", "line one\r\nline two\r\n");
        var crlf = CustomSourceLoader.Read(_root).Bundle;

        Directory.Delete(_root, recursive: true);
        Write("custom/dotnet/Pricing.cs", "line one\nline two\n");
        var lf = CustomSourceLoader.Read(_root).Bundle;

        Assert.NotNull(crlf);
        Assert.NotNull(lf);
        Assert.Equal(lf.Hash, crlf.Hash);
        Assert.DoesNotContain("\r", crlf.Files["Pricing.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_hash_is_framed_so_moving_a_boundary_changes_it()
    {
        Write("custom/dotnet/ab.cs", "c");
        var first = CustomSourceLoader.Read(_root).Bundle!.Hash;

        Directory.Delete(_root, recursive: true);
        Write("custom/dotnet/a.cs", "bc");
        var second = CustomSourceLoader.Read(_root).Bundle!.Hash;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Editing_a_body_changes_the_hash()
    {
        Write("custom/dotnet/Pricing.cs", "// a\n");
        var before = CustomSourceLoader.Read(_root).Bundle!.Hash;

        Write("custom/dotnet/Pricing.cs", "// b\n");
        var after = CustomSourceLoader.Read(_root).Bundle!.Hash;

        Assert.NotEqual(before, after);
        Assert.StartsWith("sha256:", after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_language_nothing_reads_is_refused_and_says_what_is_read()
    {
        Write("custom/python/pricing.py", "# nope\n");

        var read = CustomSourceLoader.Read(_root);

        Assert.Null(read.Bundle);
        Assert.Contains(read.Problems, p => p.Contains("nothing here reads", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_languages_are_refused_because_one_application_has_one_target()
    {
        Write("custom/dotnet/Pricing.cs", "// a\n");
        Write("custom/node/pricing.ts", "// b\n");

        var read = CustomSourceLoader.Read(_root);

        Assert.Null(read.Bundle);
        Assert.Contains(read.Problems, p => p.Contains("one language", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_language_directory_is_not_an_application_with_custom_code()
    {
        Directory.CreateDirectory(Path.Combine(_root, "custom", "dotnet"));

        var read = CustomSourceLoader.Read(_root);

        Assert.Null(read.Bundle);
        Assert.Empty(read.Problems);
    }
}
