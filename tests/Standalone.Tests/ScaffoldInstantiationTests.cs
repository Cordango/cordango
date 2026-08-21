// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// Writes the scaffold to a directory and runs the real .NET SDK over it.
///
/// <para><b>Why nothing cheaper will do.</b> Everything else in this suite tests the runtime as a
/// library, compiled by this repository's own project file. The scaffold's <c>Program.cs</c>,
/// <c>AppDbContext.cs</c> and identity setup are not compiled by anything here — they are template
/// text with placeholders in it, deliberately excluded from the build. The only thing that can say
/// whether they are valid C# that references types that exist is a compiler, pointed at the output
/// the way a user's would be. A typo in a template is invisible until then, and until then it is
/// invisible to US and obvious to the first person who generates an application.</para>
///
/// <para><b>What it does not cover: the front end.</b> <c>web/package.json</c> depends on
/// <c>@cordango/web-controls</c> at <c>file:./vendor/cordango-web-controls</c>, and nothing vendors
/// that bundle yet — the controls package builds to a <c>dist/</c> that is not committed, so
/// embedding it here would break a clean clone rather than fix anything. Vendoring lands with the
/// Vue emitters, and the web half of the acceptance check lands with it. Said out loud because a
/// test suite that quietly covers one half of a deliverable reads exactly like one that covers
/// both.</para>
/// </summary>
public class ScaffoldInstantiationTests
{
    /// <summary>Set <c>CORDANGO_SKIP_SDK_TESTS=1</c> to skip. Restore reaches the package feed, so
    /// an offline machine cannot run this — and should say so rather than fail.</summary>
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

            // The project file has to be where the emitted path said it would be. If it is not, the
            // build below fails with "no project found", which describes the symptom and not the
            // cause.
            Assert.True(File.Exists(Path.Combine(api, "ExpenseClaims.Api.csproj")),
                "The scaffold did not emit a project file at the expected path.");

            var build = await Run("dotnet", ["build", api, "--nologo", "-v", "q"], root);

            Assert.True(build.ExitCode == 0,
                "A freshly scaffolded application does not compile.\n\n" + build.Output);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Written twice into two directories, the bytes are the same.
    ///
    /// <para>The whole build contract is that a definition produces an application, not an
    /// application-shaped thing that differs by machine and minute. The scaffold is the part of that
    /// output nothing derives from the definition, so if anything is going to smuggle in a timestamp
    /// or a path, it is here.</para>
    /// </summary>
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

    /// <summary>Write the scaffold into a fresh directory outside the repository. Outside on
    /// purpose: a build inside the tree would leave obj/ and bin/ where the next test run walks.</summary>
    private static string Materialise(ScaffoldOptions options)
    {
        var root = Path.Combine(Path.GetTempPath(), "cordango-scaffold-" + Guid.NewGuid().ToString("n")[..8]);

        foreach (var file in Scaffold.Files(options))
        {
            var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // UTF-8 with no BOM and \n endings, which is what the real writer does. Writing it any
            // other way here would make this test agree with a generator that does not exist.
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
            // A build server holding a file handle is not a reason to fail a green build. The
            // directory is under the temp path and the machine will get it eventually.
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

        // The scaffold is meant to build in a plain clone with nothing set up. Inheriting this
        // repository's build-root redirection would move the output somewhere the generated project
        // does not expect and test something other than what a user gets.
        start.Environment.Remove("CORDANGO_BUILD_ROOT");

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + error);
    }
}
