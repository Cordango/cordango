// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;
using Cordango.SourceGen.DotNetVue.Emit;

namespace Cordango.Standalone.Tests;

public class GeneratedApplicationTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    [Theory]
    [InlineData("expenses")]
    [InlineData("time-off")]
    [InlineData("task-manager")]
    [InlineData("room-booking")]
    [InlineData("helpdesk")]
    [InlineData("sales-crm")]
    [InlineData("ventures")]
    [InlineData("budget-planner")]
    public async Task The_generated_application_compiles(string key)
    {
        if (Skipped) return;

        using var app = Materialise(key);

        var build = await Run("dotnet", ["build", Path.Combine(app.Root, "api"), "--nologo", "-v", "q"], app.Root);
        Assert.True(build.ExitCode == 0, $"The application generated from '{key}' does not compile.\n\n" + build.Output);
    }

    [Theory]
    [InlineData("expenses")]
    [InlineData("helpdesk")]
    [InlineData("task-manager")]
    [InlineData("room-booking")]
    public async Task The_model_snapshot_matches_the_model(string key)
    {
        if (Skipped) return;

        using var app = Materialise(key);

        var build = await Run("dotnet", ["build", Path.Combine(app.Root, "api"), "--nologo", "-v", "q"], app.Root);
        Assert.True(build.ExitCode == 0, $"The application generated from '{key}' does not compile.\n\n" + build.Output);

        var scaffold = await Run("dotnet",
            ["ef", "migrations", "add", "SnapshotCheck", "--project", "api", "--no-build", "--context", "AppDbContext"],
            app.Root);

        if (scaffold.ExitCode != 0 && scaffold.Output.Contains("ef does not exist", StringComparison.OrdinalIgnoreCase))
            return;

        Assert.True(scaffold.ExitCode == 0, "dotnet ef could not scaffold a migration.\n\n" + scaffold.Output);

        var added = System.IO.Directory.EnumerateFiles(Path.Combine(app.Root, "api", "Migrations"), "*_SnapshotCheck.cs")
            .FirstOrDefault();

        Assert.True(added is not null, "dotnet ef reported success but wrote no migration.");

        var up = Between(File.ReadAllText(added!), "protected override void Up(MigrationBuilder migrationBuilder)");
        Assert.True(up.Length == 0,
            "The generated model snapshot does not match the generated model. EF wants to change:\n\n" + up);
    }

    private static string Between(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";

        var open = source.IndexOf('{', start);
        var close = source.IndexOf("\n        }", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return "";

        return source[(open + 1)..close].Trim();
    }

    [Fact]
    public void Two_builds_of_the_same_definition_are_identical()
    {
        using var first = Materialise("expenses");
        using var second = Materialise("expenses");

        var a = Files(first.Root);
        var b = Files(second.Root);

        Assert.Equal(a.Keys.Order(StringComparer.Ordinal), b.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, content) in a)
            Assert.True(content.SequenceEqual(b[path]), $"{path} differs between two builds of the same definition.");
    }

    [Theory]
    [InlineData("expenses")]
    [InlineData("time-off")]
    [InlineData("task-manager")]
    [InlineData("room-booking")]
    [InlineData("helpdesk")]
    [InlineData("sales-crm")]
    public void Every_reference_application_generates(string key)
    {
        var result = Build(key, allowPartial: true);

        Assert.True(result.Ok, string.Join("\n", result.Errors.Select(e => e.Code + ": " + e.Message)));
        Assert.Contains(result.Files, f => f.RelativePath == "api/Program.cs");
        Assert.Contains(result.Files, f => f.RelativePath == "web/src/app.js");
        Assert.Contains(result.Files, f => f.RelativePath.StartsWith("api/Entities/", StringComparison.Ordinal));
        Assert.Contains(result.Files, f => f.RelativePath == "api/Migrations/" + MigrationEmitter.MigrationId + ".cs");
    }

    [Fact]
    public void An_application_with_unrenderable_screens_is_refused_by_default()
    {
        var strict = Build("sales-crm", allowPartial: false);
        var permitted = Build("sales-crm", allowPartial: true);

        Assert.True(permitted.Warnings.Count > 0,
            "sales-crm now builds completely, so this test needs a different application.");

        Assert.False(strict.Ok);
        Assert.All(strict.Errors, e => Assert.StartsWith("CORD230", e.Code, StringComparison.Ordinal));
        Assert.True(permitted.Ok);

        var readme = permitted.Files.Single(f => f.RelativePath == "README.md").Content;
        Assert.Contains("## Partial build", readme, StringComparison.Ordinal);
    }

    private static GenerateResult Build(string key, bool allowPartial)
    {
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null,
            $"{key} did not compile: {string.Join("; ", outcome.Errors)}");

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(),
            outcome.Manifest!,
            outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        return new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
        {
            ["allowIncomplete"] = allowPartial,
            ["seed"] = 42,
        }));
    }

    internal sealed class Materialised : IDisposable
    {
        public required string Root { get; init; }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static Materialised Materialise(string key)
    {
        var result = Build(key, allowPartial: true);
        Assert.True(result.Ok, string.Join("\n", result.Errors.Select(e => e.Code + ": " + e.Message)));

        var root = Path.Combine(Path.GetTempPath(), "cordango-app-" + Guid.NewGuid().ToString("n")[..8]);

        foreach (var file in result.Files)
        {
            var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, new UTF8Encoding(false).GetBytes(file.Content));
        }

        return new Materialised { Root = root };
    }

    private static Dictionary<string, byte[]> Files(string root) =>
        System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(root, p).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    internal static async Task Build(Materialised app)
    {
        var build = await Run("dotnet",
            ["build", Path.Combine(app.Root, "api"), "-c", "Release", "--nologo", "-v", "q"], app.Root);

        Assert.True(build.ExitCode == 0, "The generated application does not compile.\n\n" + build.Output);
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
