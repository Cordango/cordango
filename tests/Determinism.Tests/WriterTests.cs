// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.Determinism.Tests;

public sealed class WriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cordango-writer-tests", Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Out(string name) => Path.Combine(_root, name);

    private static BuildMetadataDraft Draft(params Diagnostic[] unsupported) =>
        new("sha256:abc", new CompilerInfo("cordango-compiler", "0.1"), "dotnet-vue", "0.1.0", unsupported);

    private static GenerateResult Files(params (string Path, string Content)[] files) =>
        GenerateResult.Produced([.. files.Select(f => new GeneratedFile(f.Path, f.Content))]);

    [Fact]
    public void Two_runs_produce_byte_identical_trees()
    {
        var a = Files(("src/B.cs", "b\n"), ("src/A.cs", "a\n"), ("README.md", "hello\n"));
        var b = Files(("README.md", "hello\n"), ("src/A.cs", "a\n"), ("src/B.cs", "b\n"));

        Assert.True(GeneratedFileWriter.Write(Out("one"), a, Draft()).Ok);
        Assert.True(GeneratedFileWriter.Write(Out("two"), b, Draft()).Ok);

        Assert.Equal(Fingerprint(Out("one")), Fingerprint(Out("two")));
    }

    [Fact]
    public void Regenerating_over_a_previous_build_matches_a_fresh_one()
    {
        var result = Files(("src/A.cs", "a\n"), ("README.md", "hi\n"));

        GeneratedFileWriter.Write(Out("again"), result, Draft());
        GeneratedFileWriter.Write(Out("again"), result, Draft());
        GeneratedFileWriter.Write(Out("fresh"), result, Draft());

        Assert.Equal(Fingerprint(Out("fresh")), Fingerprint(Out("again")));
    }

    [Fact]
    public void Line_endings_are_normalised()
    {
        GeneratedFileWriter.Write(Out("crlf"), Files(("a.txt", "one\r\ntwo\r\n")), Draft());
        GeneratedFileWriter.Write(Out("lf"), Files(("a.txt", "one\ntwo\n")), Draft());

        Assert.Equal(Fingerprint(Out("lf")), Fingerprint(Out("crlf")));
    }

    [Fact]
    public void Files_are_written_without_a_byte_order_mark()
    {
        GeneratedFileWriter.Write(Out("bom"), Files(("a.txt", "x\n")), Draft());

        var bytes = File.ReadAllBytes(Path.Combine(Out("bom"), "a.txt"));
        Assert.Equal("x\n"u8.ToArray(), bytes);
    }

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

    [Fact]
    public void A_non_empty_directory_without_build_metadata_is_refused()
    {
        Directory.CreateDirectory(Out("theirs"));
        File.WriteAllText(Path.Combine(Out("theirs"), "my-notes.md"), "do not delete");

        var report = GeneratedFileWriter.Write(Out("theirs"), Files(("a.txt", "x")), Draft());

        Assert.False(report.Ok);
        Assert.Equal("do not delete", File.ReadAllText(Path.Combine(Out("theirs"), "my-notes.md")));
    }

    [Fact]
    public void Regenerating_removes_files_the_generator_no_longer_produces()
    {
        GeneratedFileWriter.Write(Out("shrink"), Files(("a.txt", "a"), ("gone.txt", "g")), Draft());
        var report = GeneratedFileWriter.Write(Out("shrink"), Files(("a.txt", "a")), Draft());

        Assert.True(report.Ok);
        Assert.Contains("gone.txt", report.Deleted);
        Assert.False(File.Exists(Path.Combine(Out("shrink"), "gone.txt")));
    }

    [Fact]
    public void Regenerating_leaves_files_the_generator_never_wrote()
    {
        GeneratedFileWriter.Write(Out("mine"), Files(("a.txt", "a")), Draft());
        File.WriteAllText(Path.Combine(Out("mine"), "hand-written.md"), "mine");

        GeneratedFileWriter.Write(Out("mine"), Files(("a.txt", "a2")), Draft());

        Assert.Equal("mine", File.ReadAllText(Path.Combine(Out("mine"), "hand-written.md")));
    }

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

    [Fact]
    public void A_dry_run_reports_what_it_would_do_and_writes_nothing()
    {
        var report = GeneratedFileWriter.Write(
            Out("dry"), Files(("a.txt", "a")), Draft(), dryRun: true);

        Assert.True(report.Ok);
        Assert.Contains("a.txt", report.Written);
        Assert.False(Directory.Exists(Out("dry")));
    }

    private static string Fingerprint(string root)
    {
        var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => (Path: Path.GetRelativePath(root, p).Replace('\\', '/'), Bytes: File.ReadAllBytes(p)))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Path + " " + Convert.ToHexStringLower(SHA256.HashData(f.Bytes)));

        return string.Join("\n", lines);
    }
}
