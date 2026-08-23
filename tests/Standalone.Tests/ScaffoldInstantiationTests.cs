// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class ScaffoldInstantiationTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    [Fact]
    public async Task The_scaffold_compiles()
    {
        if (Skipped) return;

        var options = new ScaffoldOptions("Expense Claims", "expense-claims", "ExpenseClaims");
        var root = Materialise(options);

        try
        {
            var api = Path.Combine(root, "api");

            Assert.True(File.Exists(Path.Combine(api, "ExpenseClaims.Api.csproj")),
                "The scaffold did not emit a project file at the expected path.");

            RuntimeFeed.PointAt(root);

            var build = await Run("dotnet", ["build", api, "--nologo", "-v", "q"], root);

            Assert.True(build.ExitCode == 0,
                "A freshly scaffolded application does not compile.\n\n" + build.Output);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Materialising_twice_produces_identical_bytes()
    {
        var options = new ScaffoldOptions("Expense Claims", "expense-claims", "ExpenseClaims");

        var first = Materialise(options);
        var second = Materialise(options);

        try
        {
            var a = Files(first);
            var b = Files(second);

            Assert.Equal(a.Keys.Order(StringComparer.Ordinal), b.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in a)
                Assert.True(bytes.SequenceEqual(b[path]), $"{path} differs between two materialisations.");
        }
        finally
        {
            Cleanup(first);
            Cleanup(second);
        }
    }

    private static Dictionary<string, byte[]> Files(string root) =>
        System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(root, p).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static string Materialise(ScaffoldOptions options)
    {
        var root = Path.Combine(Path.GetTempPath(), "cordango-scaffold-" + Guid.NewGuid().ToString("n")[..8]);

        foreach (var file in Scaffold.Files(options))
        {
            var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            File.WriteAllBytes(target, new System.Text.UTF8Encoding(false).GetBytes(file.Content));
        }

        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string file, string[] arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        start.Environment.Remove("CORDANGO_BUILD_ROOT");

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + error);
    }
}
