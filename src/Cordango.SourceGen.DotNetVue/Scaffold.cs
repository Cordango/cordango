// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Cordango.SourceGen.DotNetVue;

/// <summary>What the scaffold needs to know about the application it is being cut for.</summary>
/// <param name="AppName">The display name, as the definition spells it: "Expense Claims".</param>
/// <param name="AppKey">The slug: database name, container name, cookie name.</param>
/// <param name="AppNamespace">The C# namespace and assembly name: "ExpenseClaims".</param>
/// <param name="PartialBuildSection">Markdown injected near the top of the generated README when a
/// build was knowingly incomplete. Empty otherwise, and empty is the only honest default: a README
/// that says nothing about being partial must mean the build was not.</param>
/// <param name="RuntimeAsPackage">Reference <c>Cordango.Standalone</c> from a feed instead of
/// checking its source in as a sibling project. The cleaner shape, and it needs the package to be
/// restorable — which it is not until it is published.</param>
public sealed record ScaffoldOptions(
    string AppName,
    string AppKey,
    string AppNamespace,
    string PartialBuildSection = "",
    bool RuntimeAsPackage = false);

/// <summary>
/// Every file a generated application has before a single entity is generated into it: the host, the
/// sign-in stack, the built-in directory, the Docker packaging, the Vue shell — and the Cordango
/// runtime itself, as source.
///
/// <para><b>The runtime is a package, and it arrives as source until that package is public.</b>
/// <c>Cordango.Standalone</c> packs and is meant to be referenced, because it is a library rather
/// than part of anybody's application. Until it can be restored, the same source is emitted as a
/// SIBLING project — <c>runtime/</c>, next to <c>api/</c>, never inside it — so the application
/// directory holds the application and nothing else. The emitted project file carries the one line
/// that swaps it, and <c>--runtime package</c> emits that shape today for anyone whose feed already
/// has it.</para>
/// </summary>
public static class Scaffold
{
    private const string RuntimePrefix = "Runtime/";
    private const string TemplatePrefix = "Template/";

    /// <summary>
    /// Where the runtime lands: its own project beside the application, not inside it.
    ///
    /// <para>It was <c>api/Cordango/</c> and that was wrong. Twenty files of framework code in the
    /// middle of somebody's application is twenty files they have to recognise as not-theirs every
    /// time they look for their own. As a sibling project it is a dependency that happens to be
    /// checked in — and the day the package is public, deleting the directory and changing one line
    /// makes it an ordinary <c>PackageReference</c>.</para>
    /// </summary>
    private const string RuntimeTarget = "runtime/";

    /// <summary>
    /// A version derived from the scaffold's own contents, not maintained by hand.
    ///
    /// <para>It goes into <c>cordango.build.json</c>, where its job is to answer "which scaffold
    /// produced this application" years later. A hand-written constant answers that only for as long
    /// as somebody remembers to bump it, and the failure is silent: two different scaffolds claiming
    /// to be the same one. Hashing the files removes the remembering.</para>
    /// </summary>
    public static string Version => Lazy.Value.Version;

    /// <summary>
    /// The version of <c>Cordango.Standalone</c> a generated application is built against.
    ///
    /// <para>One constant, used by the emitted project file, by the PackageReference the emitted
    /// project file explains how to switch to, and by the build metadata. Three places that have to
    /// agree, agreeing by construction.</para>
    /// </summary>
    public const string RuntimeVersion = "0.1.0-alpha";

    /// <summary>What the application's project file says about the runtime, in each of the two
    /// shapes. Written here rather than as two template files, because the rest of the project file
    /// is identical and two copies of it would drift.</summary>
    private const string ProjectReference =
        """
          <!--
            The Cordango runtime. Checked in as a sibling project because the package is not public
            yet; when it is, delete ../runtime/ and put this in its place:

                <PackageReference Include="Cordango.Standalone" Version="{{RuntimeVersion}}" />

            Same code, restored instead of compiled.
          -->
          <ItemGroup>
            <ProjectReference Include="..\runtime\Cordango.Standalone.csproj" />
          </ItemGroup>

        """;

    /// <summary>Every placeholder the scaffold understands, so a test can assert that none of them
    /// survives into the output rather than keeping its own list that drifts.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{{AppName}}", "{{AppKey}}", "{{AppNamespace}}",
        "{{PartialBuildSection}}", "{{RuntimeVersion}}", "{{RuntimeReference}}",
        "{{RuntimeProjectCopy}}", "{{RuntimeSourceCopy}}",
    ];

    private const string PackageReference =
        """
          <!-- The Cordango runtime: records, hooks, permissions, queries, the directory, the wire. -->
          <ItemGroup>
            <PackageReference Include="Cordango.Standalone" Version="{{RuntimeVersion}}" />
          </ItemGroup>

        """;

    /// <summary>The scaffold, cut for this application. Ordinal by path, so the caller receives the
    /// same sequence on every machine.</summary>
    public static IReadOnlyList<GeneratedFile> Files(ScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // ORDER MATTERS, and it is the one thing about this method that is not obvious. A
        // substitution's VALUE may itself contain a token — the runtime reference block quotes the
        // version — so a token that appears in a value has to be replaced after the value is
        // inserted, not before. Tokens are listed inside-out.
        var substitutions = new (string Token, string Value)[]
        {
            ("{{RuntimeReference}}", options.RuntimeAsPackage ? PackageReference : ProjectReference),
            // The Dockerfile copies what the project file references. With the runtime restored
            // from a feed there is no runtime/ directory to copy, and a COPY of a path that does
            // not exist fails the build.
            ("{{RuntimeProjectCopy}}", options.RuntimeAsPackage ? "" : "COPY runtime/*.csproj ./runtime/\n"),
            ("{{RuntimeSourceCopy}}", options.RuntimeAsPackage ? "" : "COPY runtime/ ./runtime/\n"),

            ("{{AppNamespace}}", options.AppNamespace),
            ("{{AppName}}", options.AppName),
            ("{{AppKey}}", options.AppKey),
            ("{{PartialBuildSection}}", options.PartialBuildSection),
            ("{{RuntimeVersion}}", RuntimeVersion),
        };

        var files = new List<GeneratedFile>(Lazy.Value.Files.Count);
        foreach (var (path, content) in Lazy.Value.Files)
        {
            // With the package referenced from a feed there is nothing to check in, so the runtime
            // project and its source are simply not emitted.
            if (options.RuntimeAsPackage && path.StartsWith(RuntimeTarget, StringComparison.Ordinal))
                continue;

            var target = path;
            var text = content;

            // Paths carry placeholders too — the project file is named after the application, and
            // naming it anything else would leave a user with a repository whose project does not
            // match the thing it builds.
            foreach (var (token, value) in substitutions)
            {
                target = target.Replace(token, value, StringComparison.Ordinal);
                text = text.Replace(token, value, StringComparison.Ordinal);
            }

            files.Add(new GeneratedFile(target, text));
        }

        return [.. files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)];
    }

    /// <summary>The scaffold before substitution: target path to content, already sorted, plus the
    /// version hashed from it. Read once — the resources cannot change while the process is
    /// running.</summary>
    private static readonly Lazy<(IReadOnlyList<(string Path, string Content)> Files, string Version)> Lazy =
        new(Load, isThreadSafe: true);

    private static (IReadOnlyList<(string, string)>, string) Load()
    {
        var assembly = typeof(Scaffold).Assembly;
        var files = new List<(string Path, string Content)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            // MSBuild's RecursiveDir uses the BUILD machine's separator. Normalising here is what
            // keeps a Windows-built CLI and a Linux-built one emitting the same paths.
            var normalised = name.Replace('\\', '/');

            string target;
            if (normalised.StartsWith(RuntimePrefix, StringComparison.Ordinal))
                target = RuntimeTarget + normalised[RuntimePrefix.Length..];
            else if (normalised.StartsWith(TemplatePrefix, StringComparison.Ordinal))
                target = normalised[TemplatePrefix.Length..];
            else
                continue;

            // The project file is stored with a .template suffix so that nothing in this repository
            // mistakes it for a project to build. It is a project file again on the way out.
            if (target.EndsWith(".template", StringComparison.Ordinal))
                target = target[..^".template".Length];

            files.Add((target, Read(assembly, name)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return (files, "1.0.0+" + Fingerprint(files));
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded scaffold resource '{name}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Line endings normalised on the way IN, before anything is hashed or written. The
        // repository stores these files with whatever the checkout produced, and a scaffold whose
        // version changed because somebody cloned on Windows would be a version that answers the
        // wrong question.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Fingerprint(IReadOnlyList<(string Path, string Content)> files)
    {
        using var sha = SHA256.Create();
        var buffer = new MemoryStream();

        foreach (var (path, content) in files)
        {
            // Length-prefixed rather than delimited: two files whose paths and contents could be
            // rearranged to the same byte stream would otherwise fingerprint identically.
            Write(buffer, path);
            Write(buffer, content);
        }

        return Convert.ToHexStringLower(sha.ComputeHash(buffer.ToArray()))[..12];

        static void Write(Stream to, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            to.Write(BitConverter.GetBytes(bytes.Length));
            to.Write(bytes);
        }
    }
}
