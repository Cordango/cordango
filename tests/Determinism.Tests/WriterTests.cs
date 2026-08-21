// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.Determinism.Tests;

/// <summary>
/// The runner owns the filesystem, and these are the properties that buys.
///
/// <para>Determinism is the promise the whole standalone story rests on — "same definition, same
/// versions, same bytes" is what makes a generated repository reviewable in a diff instead of
/// regenerated on faith. It is also the promise easiest to lose by accident, because nothing about
/// a wrong answer here looks wrong: files appear, the app builds, and only a byte comparison two
/// months later shows that two runs disagreed.</para>
/// </summary>
public sealed class WriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cordango-writer-tests", Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a green test over */ }
    }

    private string Out(string name) => Path.Combine(_root, name);

    private static BuildMetadataDraft Draft(params Diagnostic[] unsupported) =>
        new("sha256:abc", new CompilerInfo("cordango-compiler", "0.1"), "dotnet-vue", "0.1.0", unsupported);

    private static GenerateResult Files(params (string Path, string Content)[] files) =>
        GenerateResult.Produced([.. files.Select(f => new GeneratedFile(f.Path, f.Content))]);

    // ---- determinism ---------------------------------------------------------------------------

    /// <summary>
    /// The headline property, checked the way a person would: generate twice, hash the trees.
    ///
    /// <para>Written with the files in a DIFFERENT order the second time, because a generator that
    /// enumerates a dictionary has no order guarantee and the runner is what makes that not
    /// matter.</para>
    /// </summary>
    [Fact]
    public void Two_runs_produce_byte_identical_trees()
    {
        var a = Files(("src/B.cs", "b\n"), ("src/A.cs", "a\n"), ("README.md", "hello\n"));
        var b = Files(("README.md", "hello\n"), ("src/A.cs", "a\n"), ("src/B.cs", "b\n"));

        Assert.True(GeneratedFileWriter.Write(Out("one"), a, Draft()).Ok);
        Assert.True(GeneratedFileWriter.Write(Out("two"), b, Draft()).Ok);

        Assert.Equal(Fingerprint(Out("one")), Fingerprint(Out("two")));
    }

    /// <summary>Regenerating over yesterday's output is the ordinary case, and it has to land on the
    /// same bytes as a fresh generation — otherwise "same inputs, same output" holds only for
    /// directories nobody has used.</summary>
    [Fact]
    public void Regenerating_over_a_previous_build_matches_a_fresh_one()
    {
        var result = Files(("src/A.cs", "a\n"), ("README.md", "hi\n"));

        GeneratedFileWriter.Write(Out("again"), result, Draft());
        GeneratedFileWriter.Write(Out("again"), result, Draft());
        GeneratedFileWriter.Write(Out("fresh"), result, Draft());

        Assert.Equal(Fingerprint(Out("fresh")), Fingerprint(Out("again")));
    }

    /// <summary>A generator that emits Windows line endings must not produce a different tree from
    /// one that emits Unix endings. The repository is hashed on machines of both kinds.</summary>
    [Fact]
    public void Line_endings_are_normalised()
    {
        GeneratedFileWriter.Write(Out("crlf"), Files(("a.txt", "one\r\ntwo\r\n")), Draft());
        GeneratedFileWriter.Write(Out("lf"), Files(("a.txt", "one\ntwo\n")), Draft());

        Assert.Equal(Fingerprint(Out("lf")), Fingerprint(Out("crlf")));
    }

    /// <summary>A byte-order mark would make generated files differ from what every other tool
    /// writes, and would sit at the top of files a compiler has to read.</summary>
    [Fact]
    public void Files_are_written_without_a_byte_order_mark()
    {
        GeneratedFileWriter.Write(Out("bom"), Files(("a.txt", "x\n")), Draft());

        var bytes = File.ReadAllBytes(Path.Combine(Out("bom"), "a.txt"));
        Assert.Equal("x\n"u8.ToArray(), bytes);
    }

    // ---- path safety ---------------------------------------------------------------------------

    /// <summary>
    /// The reason a generator is handed no directory at all.
    ///
    /// <para>Each of these is a string a generator could produce by mistake or on purpose, and the
    /// consequence of accepting one is a write outside the directory the user named. They are
    /// refused before ANY file is written, so a result containing one produces nothing rather than
    /// a partial tree plus an escape.</para>
    /// </summary>
    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("src/../../escaped.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("..\\escaped.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_leaves_the_output_directory_is_refused(string path)
    {
        var report = GeneratedFileWriter.Write(Out("guard"), Files((path, "x")), Draft());

        Assert.False(report.Ok);
        Assert.Empty(report.Written);
        Assert.False(Directory.Exists(Out("guard")), "nothing at all should have been written");
    }

    [Fact]
    public void Two_files_claiming_one_path_are_refused()
    {
        var report = GeneratedFileWriter.Write(
            Out("dupe"), Files(("a.txt", "one"), ("a.txt", "two")), Draft());

        Assert.False(report.Ok);
        Assert.Contains("two files claim", string.Join(" ", report.Errors.Select(e => e.Message)));
    }

    // ---- what a regeneration owns ---------------------------------------------------------------

    /// <summary>
    /// A directory somebody else made is not ours to overwrite.
    ///
    /// <para>"Generate into an existing project" is a different feature with different rules, and
    /// guessing which one was meant is how a person loses work they cannot get back.</para>
    /// </summary>
    [Fact]
    public void A_non_empty_directory_without_build_metadata_is_refused()
    {
        Directory.CreateDirectory(Out("theirs"));
        File.WriteAllText(Path.Combine(Out("theirs"), "my-notes.md"), "do not delete");

        var report = GeneratedFileWriter.Write(Out("theirs"), Files(("a.txt", "x")), Draft());

        Assert.False(report.Ok);
        Assert.Equal("do not delete", File.ReadAllText(Path.Combine(Out("theirs"), "my-notes.md")));
    }

    /// <summary>A file the generator no longer produces has to go, or the next build carries a
    /// screen for an entity that was deleted three commits ago.</summary>
    [Fact]
    public void Regenerating_removes_files_the_generator_no_longer_produces()
    {
        GeneratedFileWriter.Write(Out("shrink"), Files(("a.txt", "a"), ("gone.txt", "g")), Draft());
        var report = GeneratedFileWriter.Write(Out("shrink"), Files(("a.txt", "a")), Draft());

        Assert.True(report.Ok);
        Assert.Contains("gone.txt", report.Deleted);
        Assert.False(File.Exists(Path.Combine(Out("shrink"), "gone.txt")));
    }

    /// <summary>And it removes ONLY those. Anything a person added — a test, a rewritten README,
    /// their own module — is not the generator's to clean up.</summary>
    [Fact]
    public void Regenerating_leaves_files_the_generator_never_wrote()
    {
        GeneratedFileWriter.Write(Out("mine"), Files(("a.txt", "a")), Draft());
        File.WriteAllText(Path.Combine(Out("mine"), "hand-written.md"), "mine");

        GeneratedFileWriter.Write(Out("mine"), Files(("a.txt", "a2")), Draft());

        Assert.Equal("mine", File.ReadAllText(Path.Combine(Out("mine"), "hand-written.md")));
    }

    // ---- the metadata --------------------------------------------------------------------------

    [Fact]
    public void The_build_metadata_records_a_hash_for_every_file()
    {
        GeneratedFileWriter.Write(Out("meta"), Files(("a.txt", "a\n"), ("b/c.txt", "c\n")), Draft());

        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Out("meta"), BuildMetadata.FileName)))!.AsObject();
        var files = doc["files"]!.AsArray();

        Assert.Equal(2, files.Count);
        Assert.Equal("a.txt", files[0]!["path"]!.GetValue<string>());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("a\n"))),
            files[0]!["sha256"]!.GetValue<string>());
    }

    /// <summary>
    /// A knowingly partial build says so in the artifact, permanently.
    ///
    /// <para>A warning printed once scrolls away. Six months later somebody inherits the repository
    /// and has no way to tell a deliberate subset from a finished build — which is exactly when it
    /// matters, because they are about to rely on a screen that was never generated.</para>
    /// </summary>
    [Fact]
    public void A_partial_build_is_recorded_rather_than_only_warned_about()
    {
        var skipped = new Diagnostic("CORD2111", "'gantt' block: not generated yet", "$.pages[1].blocks[0]");

        GeneratedFileWriter.Write(Out("partial"), Files(("a.txt", "a")), Draft(skipped));

        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Out("partial"), BuildMetadata.FileName)))!.AsObject();

        Assert.True(doc["partial"]!.GetValue<bool>());
        var listed = doc["unsupportedCapabilities"]!.AsArray();
        Assert.Single(listed);
        Assert.Equal("CORD2111", listed[0]!["code"]!.GetValue<string>());
        Assert.Equal("$.pages[1].blocks[0]", listed[0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_complete_build_says_it_is_not_partial()
    {
        GeneratedFileWriter.Write(Out("whole"), Files(("a.txt", "a")), Draft());

        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Out("whole"), BuildMetadata.FileName)))!.AsObject();

        Assert.False(doc["partial"]!.GetValue<bool>());
        Assert.Empty(doc["unsupportedCapabilities"]!.AsArray());
    }

    /// <summary>The metadata is written on every run and is the most tempting place to record
    /// "generated at". A clock in there would end the determinism claim in the very file that
    /// documents it.</summary>
    [Fact]
    public void The_build_metadata_carries_no_clock_and_no_machine()
    {
        GeneratedFileWriter.Write(Out("clock"), Files(("a.txt", "a")), Draft());
        var text = File.ReadAllText(Path.Combine(Out("clock"), BuildMetadata.FileName));

        foreach (var smell in new[] { "generatedAt", "timestamp", DateTime.UtcNow.Year.ToString() })
            Assert.DoesNotContain(smell, text, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- dry run -------------------------------------------------------------------------------

    [Fact]
    public void A_dry_run_reports_what_it_would_do_and_writes_nothing()
    {
        var report = GeneratedFileWriter.Write(
            Out("dry"), Files(("a.txt", "a")), Draft(), dryRun: true);

        Assert.True(report.Ok);
        Assert.Contains("a.txt", report.Written);
        Assert.False(Directory.Exists(Out("dry")));
    }

    /// <summary>Relative path → sha256 of the bytes on disk, sorted. Two trees with the same
    /// fingerprint are the same tree, including which files exist.</summary>
    private static string Fingerprint(string root)
    {
        var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (Path: Path.GetRelativePath(root, p).Replace('\\', '/'), Bytes: File.ReadAllBytes(p)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Path + " " + Convert.ToHexStringLower(SHA256.HashData(f.Bytes)));

        return string.Join("\n", lines);
    }
}
