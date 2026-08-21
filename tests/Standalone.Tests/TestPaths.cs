// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Tests;

/// <summary>Finding this repository from a test assembly, wherever the build put it.</summary>
internal static class TestPaths
{
    /// <summary>
    /// Walk up until the repository is recognisable, anchored on the schema — the one file that is
    /// certainly here and certainly nowhere else.
    ///
    /// <para>Deliberately not a relative hop from the test binary: the number of directories between
    /// <c>bin/</c> and the root is a fact about the SDK's layout, and it has changed before.</para>
    /// </summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "schemas", "app-definition.schema.json")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root above " + AppContext.BaseDirectory
            + ". Do not set CORDANGO_BUILD_ROOT when running tests — it moves the test assembly outside the repository, "
            + "and this failure names a missing root without hinting that an environment variable caused it.");
    }
}
