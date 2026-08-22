// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class PersistedStateTests
{
    private static readonly IReadOnlyDictionary<string, string> Files =
        Scaffold.Files(new ScaffoldOptions("Expenses", "expenses", "Expenses"))
            .ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

    [Fact]
    public void The_host_keeps_its_key_ring_where_it_was_told_to()
    {
        var program = Files["api/Program.cs"];

        Assert.Contains("Storage:Keys", program, StringComparison.Ordinal);
        Assert.Contains("PersistKeysToFileSystem", program, StringComparison.Ordinal);

        Assert.Contains("SetApplicationName", program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_creates_both_directories_and_names_them()
    {
        var dockerfile = Files["Dockerfile"];

        Assert.Contains("/appdata/media /appdata/keys", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Storage__Path=/appdata/media", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Storage__Keys=/appdata/keys", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_volume_covers_everything_the_image_writes()
    {
        var compose = Files["docker-compose.yml"];

        Assert.Contains(":/appdata\n", compose, StringComparison.Ordinal);
        Assert.DoesNotContain(":/appdata/", compose, StringComparison.Ordinal);
    }
}
