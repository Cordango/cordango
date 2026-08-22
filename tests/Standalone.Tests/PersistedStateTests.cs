// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// What a rebuild must not take with it.
///
/// <para>A generated application writes two things outside its database: uploaded files, and the
/// data protection key ring its session and antiforgery cookies are signed with. ASP.NET Core keeps
/// the second in the user's home directory by default — which, in a container, is INSIDE the
/// container. Everything works until the image is rebuilt, and then everybody is signed out and the
/// first POST from an already-open tab comes back "your session token is out of date". Nothing logs
/// an error, because nothing went wrong: the keys those cookies were written with no longer
/// exist.</para>
///
/// <para>Three files have to agree for that not to happen, and they are the kind of three that drift:
/// the host chooses a path, the image creates it, the compose file puts a volume over it. The
/// original bug was exactly a disagreement of this shape — the volume was mounted at
/// <c>/appdata/media</c>, so anything else written under <c>/appdata</c> was never persisted at
/// all.</para>
/// </summary>
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

        // The application name is part of the purpose string every protected value carries, and it
        // defaults to the content root PATH — so without this, running the same application from a
        // different directory invalidates every cookie for no visible reason.
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

    /// <summary>
    /// The volume covers everything the image writes, not one directory inside it.
    ///
    /// <para>This is the assertion that would have caught the original bug. A mount at
    /// <c>/appdata/media</c> persists uploads and silently discards anything else put under
    /// <c>/appdata</c> — which reads as correct right up until something else is put there.</para>
    /// </summary>
    [Fact]
    public void The_volume_covers_everything_the_image_writes()
    {
        var compose = Files["docker-compose.yml"];

        Assert.Contains(":/appdata\n", compose, StringComparison.Ordinal);
        Assert.DoesNotContain(":/appdata/", compose, StringComparison.Ordinal);
    }
}
