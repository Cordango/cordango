// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.Node;

/// <summary>What the scaffold needs to know about the application it is being cut for.</summary>
/// <param name="AppName">The display name, as the definition spells it: "Expense Claims".</param>
/// <param name="AppKey">The slug: database name, container name, cookie name.</param>
/// <param name="PartialBuildSection">Markdown injected near the top of the generated README when a
/// build was knowingly incomplete. Empty otherwise, and empty is the only honest default: a README
/// that says nothing about being partial must mean the build was not.</param>
public sealed record ScaffoldOptions(
    string AppName,
    string AppKey,
    string PartialBuildSection = "");

/// <summary>
/// Every file a generated application has before a single entity is generated into it: the host,
/// the packaging, and the Vue shell.
///
/// <para><b>The runtime is a package, and there is no source shape.</b> <c>dotnet-vue</c> can emit
/// <c>Cordango.Standalone</c> as a sibling project, because a .NET solution can reference a project
/// in the same tree and the package may not be on a feed yet. Node has neither problem:
/// <c>@cordango/standalone</c> is a dependency line and <c>npm install</c> is the whole of it. An
/// application that wants to work ON the runtime clones this repository and links it, which is what
/// <c>npm link</c> is for and does not need a generator flag.</para>
/// </summary>
public static class Scaffold
{
    private const string TemplatePrefix = "Template/";

    /// <summary>
    /// A version derived from the scaffold's own contents, not maintained by hand.
    ///
    /// <para>It goes into <c>cordango.build.json</c>, where its job is to answer "which scaffold
    /// produced this application" years later. A hand-written constant answers that only for as long
    /// as somebody remembers to bump it, and the failure is silent: two different scaffolds claiming
    /// to be the same one.</para>
    /// </summary>
    public static string Version => Lazy.Value.Version;

    /// <summary>The version of <c>@cordango/standalone</c> a generated application is built against.
    /// THE SAME NUMBER AS THIS GENERATOR, because they are published from one tag and cannot be
    /// installed apart.</summary>
    public static string RuntimeVersion => BuildVersion.Current;

    /// <summary>The version of <c>@cordango/web-controls</c> the front end is built against. The pin
    /// and its reasoning live with the shared Vue shell in <see cref="WebScaffold"/>.</summary>
    public static string WebControlsVersion => WebScaffold.WebControlsVersion;

    /// <summary>Every placeholder the scaffold understands, so a test can assert that none of them
    /// survives into the output rather than keeping its own list that drifts.</summary>
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{{AppName}}", "{{AppKey}}", "{{AppSlug}}",
        "{{PartialBuildSection}}", "{{RuntimeVersion}}", "{{WebControlsVersion}}",
        "{{WebOutDir}}", "{{DevApiOrigin}}",
    ];

    /// <summary>
    /// The application's key as Docker will accept it: lower case, hyphens rather than underscores.
    ///
    /// <para>It names the COMPOSE PROJECT, and that is not cosmetic. Compose takes the project name
    /// from the directory it is run in unless the file says otherwise — and every application this
    /// generator produces is built into a directory somebody called <c>generated</c>.</para>
    /// </summary>
    private static string Slug(string appKey) => appKey.Replace('_', '-').ToLowerInvariant();

    /// <summary>The scaffold, cut for this application. Ordinal by path, so the caller receives the
    /// same sequence on every machine.</summary>
    public static IReadOnlyList<GeneratedFile> Files(ScaffoldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var substitutions = new (string Token, string Value)[]
        {
            ("{{WebControlsVersion}}", WebControlsVersion),
            ("{{RuntimeVersion}}", RuntimeVersion),

            // Where the built front end lands, and where `npm run dev` proxies to. Both live in the
            // SHARED vite config, which every standalone target emits — so the two facts that
            // really are about the backend are substituted rather than hard-coded into a file this
            // target does not own alone.
            //
            // `api/public` rather than ASP.NET Core's `api/wwwroot`: nothing in Node treats a
            // directory name as meaningful, so it is named after what it holds.
            ("{{WebOutDir}}", "../api/public"),
            ("{{DevApiOrigin}}", "http://localhost:5000"),

            ("{{AppName}}", options.AppName),
            ("{{AppSlug}}", Slug(options.AppKey)),
            ("{{AppKey}}", options.AppKey),
            ("{{PartialBuildSection}}", options.PartialBuildSection),
        };

        var files = new List<GeneratedFile>(Lazy.Value.Files.Count);

        foreach (var (path, content) in Lazy.Value.Files)
        {
            var target = path;
            var text = content;

            foreach (var (token, value) in substitutions)
            {
                target = target.Replace(token, value, StringComparison.Ordinal);
                text = text.Replace(token, value, StringComparison.Ordinal);
            }

            files.Add(new GeneratedFile(target, text));
        }

        return [.. files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)];
    }

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
            if (!normalised.StartsWith(TemplatePrefix, StringComparison.Ordinal)) continue;

            var target = normalised[TemplatePrefix.Length..];

            // package.json is stored with a .template suffix so that nothing in this repository
            // mistakes it for a package to install. It is a package.json again on the way out.
            if (target.EndsWith(".template", StringComparison.Ordinal))
                target = target[..^".template".Length];

            files.Add((target, Read(assembly, name)));
        }

        // The Vue shell lives in Cordango.SourceGen.Common — it is the half every standalone target
        // shares. Merged here before the sort, so the file order and the fingerprint cover the whole
        // scaffold rather than only this target's part of it.
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
