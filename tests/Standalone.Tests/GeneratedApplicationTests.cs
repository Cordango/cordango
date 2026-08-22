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

/// <summary>
/// Generate a real application from a real definition, and make the toolchain that will build it
/// say whether it works.
///
/// <para><b>Nothing short of this proves anything.</b> A generator can be tested by asserting on the
/// strings it produces, and those tests pass happily while the output does not compile: a missing
/// using, a name that collides with its own type, a nullable mismatch EF cares about. The compiler
/// and EF already know the answer, so the test asks them.</para>
///
/// <para>Set <c>CORDANGO_SKIP_SDK_TESTS=1</c> to skip. These shell out to the .NET SDK and reach the
/// package feed, so a machine with neither should say so rather than fail.</para>
/// </summary>
public class GeneratedApplicationTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    /// <summary>
    /// EVERY reference application compiles, not just the one that happened to be the test subject.
    ///
    /// <para>This was a <c>[Fact]</c> over <c>expenses</c> alone, and it stayed green through three
    /// defects that each stopped a different application from building: an entity named <c>task</c>
    /// is ambiguous with <c>System.Threading.Tasks.Task</c>; an entity whose name matches the
    /// application's own namespace resolves to the NAMESPACE at every use site; and an application
    /// where no entity has an auto-filled field has no <c>Hooks</c> namespace for
    /// <c>AppSetup.cs</c> to import. None of them is exotic — "task" is the most ordinary noun in
    /// project management — and none was visible in the one application being compiled.</para>
    ///
    /// <para>Six compiles, a couple of seconds each. That is the price of knowing.</para>
    /// </summary>
    [Theory]
    [InlineData("expenses")]
    [InlineData("time-off")]
    [InlineData("task-manager")]
    [InlineData("room-booking")]
    [InlineData("helpdesk")]
    [InlineData("sales-crm")]
    // The two that place a `history` block. They used to be REFUSED outright — one card on one
    // screen stopped an entire application from being generated. Now the block becomes a card
    // saying so, the gap is written into the README and the build metadata, and the other ninety
    // per cent of the application is there. These two being here is the proof of that.
    [InlineData("ventures")]
    // And the largest specimen there is: 73 computed fields, 8 workflows, two of which lay out
    // grids. It is the acceptance case for the calculation work, and until the capability gate
    // stopped refusing it, it could not be generated at all.
    [InlineData("budget-planner")]
    public async Task The_generated_application_compiles(string key)
    {
        if (Skipped) return;

        using var app = Materialise(key);

        var build = await Run("dotnet", ["build", Path.Combine(app.Root, "api"), "--nologo", "-v", "q"], app.Root);
        Assert.True(build.ExitCode == 0, $"The application generated from '{key}' does not compile.\n\n" + build.Output);
    }

    /// <summary>
    /// The model snapshot describes the generated model EXACTLY.
    ///
    /// <para>Asked by running <c>dotnet ef migrations add</c> against the generated project: EF diffs
    /// its model against our snapshot, and an EMPTY migration is EF saying the two agree. Anything in
    /// the <c>Up</c> method is a difference we shipped, and it would surface as the user's very first
    /// migration being full of <c>AlterColumn</c> calls that change nothing.</para>
    ///
    /// <para>It found two real ones while it was being written: the snapshot spelled a nullable
    /// <c>DateTimeOffset?</c> as <c>DateTimeOffset</c>, and the directory tables described here had
    /// drifted from the ones <c>Cordango.Standalone</c> configures. Neither is visible by reading.</para>
    ///
    /// <para>Asked of several applications rather than one, because what the snapshot has to spell
    /// correctly depends on which field TYPES an application uses — a json column, a multiselect, a
    /// nullable date. One application exercises a handful of them.</para>
    /// </summary>
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

        // No EF tools on this machine is not a failure of the generator.
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

    /// <summary>The body of the first method after the given signature, trimmed. Enough to tell an
    /// empty migration from one with work in it, without parsing C#.</summary>
    private static string Between(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "";

        var open = source.IndexOf('{', start);
        var close = source.IndexOf("\n        }", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return "";

        return source[(open + 1)..close].Trim();
    }

    /// <summary>
    /// Two builds of the same definition produce the same bytes.
    ///
    /// <para>The property the whole toolchain rests on. Anything that broke it — a timestamp, a
    /// dictionary iterated in hash order, a machine path — would break it silently, and the symptom
    /// would be a diff nobody could account for on a colleague's machine.</para>
    /// </summary>
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

    /// <summary>Every reference app in the corpus generates without the emitters falling over. Not
    /// that each one COMPILES — that is minutes per app — but that the generator produces a complete
    /// file set for each and says what it could not render.</summary>
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

    /// <summary>
    /// An application whose screens this target cannot fully render is REFUSED unless somebody said
    /// otherwise.
    ///
    /// <para>The alternative — emit what can be emitted and skip the rest — produces an application
    /// that looks finished and is not, and the person who inherits it has no way to tell. The
    /// permission to ship one anyway is a flag with a name, and it leaves a mark in the README and in
    /// the build metadata.</para>
    /// </summary>
    [Fact]
    public void An_application_with_unrenderable_screens_is_refused_by_default()
    {
        var strict = Build("sales-crm", allowPartial: false);
        var permitted = Build("sales-crm", allowPartial: true);

        // If this app ever builds completely the test has stopped testing anything, so it says so
        // rather than passing quietly.
        Assert.True(permitted.Warnings.Count > 0,
            "sales-crm now builds completely, so this test needs a different application.");

        Assert.False(strict.Ok);
        Assert.All(strict.Errors, e => Assert.StartsWith("CORD230", e.Code, StringComparison.Ordinal));
        Assert.True(permitted.Ok);

        var readme = permitted.Files.Single(f => f.RelativePath == "README.md").Content;
        Assert.Contains("## Partial build", readme, StringComparison.Ordinal);
    }

    // ---- running the generator ------------------------------------------------------------------

    private static GenerateResult Build(string key, bool allowPartial)
    {
        // The reference apps live in one directory and the two larger specimens — crm and
        // budget-planner — sit beside it. Falling back rather than taking a second argument keeps
        // every call site reading as just the application's name.
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        // A fixed build time, so nothing in the artifact depends on when the test ran.
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

    /// <summary>A generated application on disk, cleaned up afterwards.</summary>
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
                // A build server holding a handle is not a reason to fail a green run.
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

    /// <summary>Build a materialised application in Release, so a test that then LOADS it gets the
    /// same output the release channel would produce. Fails loudly rather than returning a code
    /// nobody checks.</summary>
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

        // The generated application is meant to build with nothing set up. Inheriting this
        // repository's build-root redirection would test something other than what a user gets.
        start.Environment.Remove("CORDANGO_BUILD_ROOT");

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + error);
    }
}
