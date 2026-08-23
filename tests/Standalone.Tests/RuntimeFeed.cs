// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// Cordango.Standalone, packed out of this working tree and served from a folder.
///
/// <para>A generated application references the runtime by version, and that version is this
/// repository's own. So on the day the version is bumped, and on every day somebody edits the runtime
/// before it ships, the version a generated application asks for is one nuget.org has never heard of.
/// Testing against the feed would mean the tree could only be tested after it was published, which is
/// the wrong way round.</para>
///
/// <para><b>The global packages folder is emptied of this one package first.</b> NuGet caches by id
/// and version, so a second run with the same version and different code would restore yesterday's
/// bytes out of <c>~/.nuget/packages</c> and report a pass on code nobody built. Nothing else is
/// touched.</para>
/// </summary>
internal static class RuntimeFeed
{
    private static readonly Lazy<string> Packed = new(Pack, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Add the feed to an application that has been written to disk but not yet built. No
    /// <c>&lt;clear/&gt;</c>: everything else it needs still comes from nuget.org.</summary>
    public static void PointAt(string appRoot)
    {
        File.WriteAllText(Path.Combine(appRoot, "NuGet.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="cordango-local" value="{Packed.Value}" />
              </packageSources>
            </configuration>

            """);
    }

    private static string Pack()
    {
        var root = TestPaths.RepoRoot();
        var feed = Path.Combine(Path.GetTempPath(), "cordango-runtime-feed");

        if (System.IO.Directory.Exists(feed)) Delete(feed);
        System.IO.Directory.CreateDirectory(feed);

        Delete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "cordango.standalone", Scaffold.RuntimeVersion));

        var pack = Run("dotnet",
            [
                "pack", Path.Combine(root, "src", "Cordango.Standalone", "Cordango.Standalone.csproj"),
                "-c", "Release", "--nologo", "-v", "q", "-o", feed,
            ],
            root);

        if (pack.ExitCode != 0)
            throw new InvalidOperationException("Could not pack the runtime for the tests.\n\n" + pack.Output);

        return feed;
    }

    private static void Delete(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static (int ExitCode, string Output) Run(string file, string[] arguments, string workingDirectory)
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output + error);
    }
}
