// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class LicenceHeaderTests
{
    private const string Marker = "SPDX-License-Identifier: Apache-2.0";

    private static bool IsExampleApplicationSource(string relative) =>
        relative.Contains("/corpus/semantic/", StringComparison.Ordinal)
        && (relative.EndsWith(".cord.ts", StringComparison.Ordinal)
            || relative.EndsWith("cord.config.ts", StringComparison.Ordinal));

    private static bool IsScaffoldTemplate(string relative) =>
        relative.Contains("/Cordango.Standalone/Templates/", StringComparison.Ordinal);

    public static TheoryData<string> SourceFiles()
    {
        var root = Corpus.RepoRoot();
        var data = new TheoryData<string>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal)
                || relative.StartsWith(".git", StringComparison.Ordinal))
                continue;

            var extension = Path.GetExtension(path);
            if (extension is not (".cs" or ".py" or ".js" or ".mjs" or ".ts" or ".vue")) continue;
            if (IsExampleApplicationSource(relative)) continue;
            if (IsScaffoldTemplate(relative)) continue;

            data.Add(relative);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SourceFiles))]
    public void Every_source_file_carries_the_licence_header(string relative)
    {
        var path = Path.Combine(Corpus.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

        var head = string.Join('\n', File.ReadLines(path).Take(8));

        Assert.True(head.Contains(Marker, StringComparison.Ordinal),
            relative + " has no licence header. Every source file in this repository carries one — "
            + "see any neighbouring file for the four lines, and note that a shebang stays first.");
    }

    [Fact]
    public void The_sweep_actually_finds_the_repository()
    {
        Assert.True(SourceFiles().Count > 150, "expected the whole source tree, got " + SourceFiles().Count);
    }
}
