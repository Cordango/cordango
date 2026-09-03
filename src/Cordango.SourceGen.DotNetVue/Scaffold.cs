// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue;

/// <summary>What the scaffold needs to know about the application it is being cut for.</summary>
/// <param name="AppName">The display name, as the definition spells it: "Expense Claims".</param>
/// <param name="AppKey">The slug: database name, container name, cookie name.</param>
/// <param name="AppNamespace">The C# namespace and assembly name: "ExpenseClaims".</param>
/// <param name="PartialBuildSection">Markdown injected near the top of the generated README when a
/// build was knowingly incomplete. Empty otherwise, and empty is the only honest default: a README
/// that says nothing about being partial must mean the build was not.</param>
/// <param name="RuntimeAsPackage">Reference <c>Cordango.Standalone</c> from a feed, which is the
/// default. Turn it off to check the runtime's source in as a sibling project instead — useful for
/// working on the runtime itself, or for building against a version no feed has yet.</param>
public sealed record ScaffoldOptions(
    string AppName,
    string AppKey,
    string AppNamespace,
    string PartialBuildSection = "",
    bool RuntimeAsPackage = true);

/// <summary>
/// Every file a generated application has before a single entity is generated into it: the host, the
/// sign-in stack, the built-in directory, the Docker packaging, and the Vue shell.
///
/// <para><b>The runtime is a package.</b> <c>Cordango.Standalone</c> is a library rather than part of
/// anybody's application, so a generated repository references it and contains only the user's own
/// code. It was emitted as source while the package was unpublished, which put twenty files of
/// framework code in front of somebody looking for their own.</para>
///
/// <para><c>--runtime source</c> still emits it, as a SIBLING project — <c>runtime/</c>, beside
/// <c>api/</c>, never inside it. That is the shape for working on the runtime itself, for building
/// against a version no feed has yet, and for anyone who would rather own the code than restore it.
/// The licence does not change either way.</para>
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
    /// <para>THE SAME NUMBER AS THIS GENERATOR, because they are published from one tag and cannot
    /// be installed apart. It was a hand-maintained constant and it was already wrong — it named
    /// 0.1.0-alpha after the package had moved on, so a generated project file referenced a version
    /// that does not exist on the feed and would have failed to restore.</para>
    /// </summary>
    public static string RuntimeVersion => BuildVersion.Current;

    /// <summary>The version of <c>@cordango/web-controls</c> the front end is built against. The pin
    /// and its reasoning live with the shared Vue shell in <see cref="WebScaffold"/>; this forwards
    /// so existing callers keep one name for it.</summary>
    public static string WebControlsVersion => WebScaffold.WebControlsVersion;

    /// <summary>What the application's project file says about the runtime, in each of the two
    /// shapes. Written here rather than as two template files, because the rest of the project file
    /// is identical and two copies of it would drift.</summary>
    private const string ProjectReference =
        """
          <!--
            The Cordango runtime, built from the source in ../runtime/ rather than restored. This is
            what `runtime: source` asks for; the ordinary shape is

                <PackageReference Include="Cordango.Standalone" Version="{{RuntimeVersion}}" />

            Same code, restored instead of compiled. Swapping back is deleting ../runtime/ and this
            ItemGroup.
          -->
          <ItemGroup>
            <ProjectReference Include="..\runtime\Cordango.Standalone.csproj" />
          </ItemGroup>

        """;

    /// <summary>Every placeholder the scaffold understands, so a test can assert that none of them
    /// survives into the output rather than keeping its own list that drifts.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{{AppName}}", "{{AppKey}}", "{{AppSlug}}", "{{AppNamespace}}",
        "{{PartialBuildSection}}", "{{RuntimeVersion}}", "{{RuntimeReference}}",
        "{{RuntimeProjectCopy}}", "{{RuntimeSourceCopy}}",
        "{{RuntimeLayout}}", "{{RuntimeLicence}}", "{{WebControlsVersion}}",
    ];

    /// <summary>
    /// The application's key as Docker will accept it: lower case, hyphens rather than underscores.
    ///
    /// <para>It names the COMPOSE PROJECT, and that is not cosmetic. Compose takes the project name
    /// from the directory it is run in unless the file says otherwise — and every application this
    /// generator produces is built into a directory somebody called <c>generated</c>. So two
    /// applications on one machine were both the project <c>generated</c>: the same container names,
    /// the same network, and — the one that loses data — the same <c>generated_db</c> volume. The
    /// second `docker compose up` did not start a second application. It adopted the first one's
    /// database.</para>
    /// </summary>
    private static string Slug(string appKey) =>
        appKey.Replace('_', '-').ToLowerInvariant();

    private const string PackageReference =
        """
          <!-- The Cordango runtime: records, hooks, permissions, queries, the directory, the wire. -->
          <ItemGroup>
            <PackageReference Include="Cordango.Standalone" Version="{{RuntimeVersion}}" />
          </ItemGroup>

        """;

    /// <summary>The generated README describes a directory listing, so a directory that is not there
    /// must not be in it. Both halves live here rather than in two README templates, because the rest
    /// of that file is long, identical, and would drift.</summary>
    private const string RuntimeLayoutSource =
        """
        runtime/              the Cordango runtime — a library, not your code (Apache-2.0).
                              Source rather than a package, because you asked for --runtime source

        """;

    private const string RuntimeLicenceSource =
        """
        `runtime/` is the Cordango runtime: our code, licensed under Apache-2.0, with its own file
        headers and its own project file. It is here as source because this application was built
        with `--runtime source`; `api/{{AppNamespace}}.Api.csproj` carries the one line that swaps it
        back for a `PackageReference`. Keep it, change it, or replace it. Nothing outside `runtime/`
        is ours.
        """;

    private const string RuntimeLicencePackage =
        """
        The one dependency that is ours is `Cordango.Standalone`, the runtime, under Apache-2.0. It
        is a NuGet package like any other and its source is public, so you can read it, fork it or
        replace it: https://github.com/cordango/cordango. Build with `--runtime source` and it
        arrives as a project in this repository instead. Nothing else here is ours.
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
            ("{{WebControlsVersion}}", WebControlsVersion),
            ("{{RuntimeReference}}", options.RuntimeAsPackage ? PackageReference : ProjectReference),
            // The Dockerfile copies what the project file references. With the runtime restored
            // from a feed there is no runtime/ directory to copy, and a COPY of a path that does
            // not exist fails the build.
            ("{{RuntimeProjectCopy}}", options.RuntimeAsPackage ? "" : "COPY runtime/*.csproj ./runtime/\n"),
            ("{{RuntimeSourceCopy}}", options.RuntimeAsPackage ? "" : "COPY runtime/ ./runtime/\n"),
            ("{{RuntimeLayout}}", options.RuntimeAsPackage ? "" : RuntimeLayoutSource),
            ("{{RuntimeLicence}}", options.RuntimeAsPackage ? RuntimeLicencePackage : RuntimeLicenceSource),

            ("{{AppNamespace}}", options.AppNamespace),
            ("{{AppName}}", options.AppName),
            ("{{AppSlug}}", Slug(options.AppKey)),
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

        // The Vue shell lives in Cordango.SourceGen.Common — it is the half every standalone target
        // shares. Merged here before the sort, so the file order and the fingerprint are exactly what
        // they were when the web tree was embedded in this assembly: the scaffold version answers
        // "which scaffold produced this application", and moving a file between assemblies did not
        // change the scaffold.
        files.AddRange(WebScaffold.Files);

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
