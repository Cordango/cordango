// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using Cordango.Cli.Generate;
using Cordango.SourceGen;

namespace Cordango.Cli.Workspace;

/// <summary>What one app's <c>custom/</c> directory holds, or what is wrong with it.</summary>
public sealed record CustomSources(CustomSourceBundle? Bundle, IReadOnlyList<string> Problems)
{
    public static readonly CustomSources None = new(null, []);
}

/// <summary>
/// Reads <c>custom/&lt;language&gt;/</c> into a bundle, and hashes it once.
///
/// <para><b>The language is a property of the WORKSPACE, not of the build target.</b> It is the
/// name of the directory somebody put their code in. That is what keeps the canonical App
/// Definition target-independent, and it is what lets a bare <c>cordango check</c> — which has no
/// target at all, by design — type-check an expression that calls custom code.</para>
///
/// <para><b>Normalised and hashed HERE, once.</b> The definition records the hash and the generator
/// verifies it against the bytes it was handed, so those two have to be the same bytes. Reading the
/// files again later would be safe but pointless, and would turn a save between check and build
/// into a refusal nobody could explain.</para>
/// </summary>
public static class CustomSourceLoader
{
    /// <summary>The directory, beside <c>entities/</c> and <c>workflows/</c>.</summary>
    public const string DirectoryName = "custom";

    /// <summary>Written by <c>cordango build</c> for the editor's benefit and read by nothing:
    /// copies of the generated entity types so an IDE can resolve them. Excluded because hashing
    /// them would make the custom code change every time an unrelated entity did — and copying
    /// them into the generated application would duplicate every entity type against the ones it
    /// already emits, which does not compile.</summary>
    private static readonly IReadOnlySet<string> Ignored =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".generated", "bin", "obj" };

    public static CustomSources Read(string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(appDirectory);

        var root = Path.Combine(appDirectory, DirectoryName);
        if (!Directory.Exists(root)) return CustomSources.None;

        var problems = new List<string>();

        var languages = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !Ignored.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (languages.Count == 0) return CustomSources.None;

        if (languages.Count > 1)
        {
            problems.Add($"{DirectoryName}/: holds {string.Join(" and ", languages)}. An "
                + "application is built by one target, in one language, so it can carry custom code "
                + "in one. Keep the directory for the language you build, and move the other out.");
            return new CustomSources(null, problems);
        }

        var language = languages[0];
        var scanner = Targets.All.OfType<ICustomCodeScanner>()
            .FirstOrDefault(s => string.Equals(s.Language, language, StringComparison.Ordinal));

        if (scanner is null)
        {
            var known = Targets.All.OfType<ICustomCodeScanner>()
                .Select(s => s.Language).OrderBy(l => l, StringComparer.Ordinal).ToList();

            problems.Add($"{DirectoryName}/{language}/: nothing here reads '{language}'. "
                + (known.Count == 0
                    ? "No target in this build takes custom code."
                    : $"Custom code is read for: {string.Join(", ", known)}."));
            return new CustomSources(null, problems);
        }

        var directory = Path.Combine(root, language);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var byLowered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Sources(directory, scanner.SourceExtension, problems))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');

            // Two paths differing only in case work on Linux and cannot be checked out reliably on
            // Windows, where the second clobbers the first. Refused while somebody is looking.
            if (byLowered.TryGetValue(relative, out var first))
            {
                problems.Add($"{DirectoryName}/{language}/: '{relative}' and '{first}' differ only "
                    + "by case. One of them would not survive a checkout on Windows.");
                continue;
            }

            byLowered[relative] = relative;
            files[relative] = File.ReadAllText(file).Replace("\r\n", "\n");
        }

        if (problems.Count > 0) return new CustomSources(null, problems);
        if (files.Count == 0) return CustomSources.None;

        return new CustomSources(new CustomSourceBundle(language, Hash(files), files), []);
    }

    /// <summary>
    /// Every source under the directory, recursively.
    ///
    /// <para>Recursive because people organise: <c>Pricing/</c>, <c>Hooks/</c>, <c>Validation/</c>.
    /// Walked by hand rather than with <c>SearchOption.AllDirectories</c> so that a symlinked
    /// directory is stepped over instead of followed — otherwise a stray
    /// <c>custom/dotnet/shared -&gt; ../../elsewhere</c> silently pulls files that are not this
    /// app's custom code into its hash and into the application it generates.</para>
    /// </summary>
    private static IEnumerable<string> Sources(string directory, string extension, List<string> problems)
    {
        var root = Path.GetFullPath(directory);
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var child in Directory.EnumerateDirectories(current).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (Ignored.Contains(name)) continue;

                if (new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    problems.Add($"'{Path.GetRelativePath(root, child).Replace('\\', '/')}' is a "
                        + "link. Custom code is the files in this directory, so a link out of it is "
                        + "not followed — move the code in, or reference it as a package.");
                    continue;
                }

                pending.Push(child);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*" + extension)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                // Re-checked as a resolved path, not only as a name: normalisation has more corner
                // cases than a rule does, and being wrong here means reading somebody else's files.
                var resolved = Path.GetFullPath(file);
                if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                yield return resolved;
            }
        }
    }

    /// <summary>
    /// The bundle's identity.
    ///
    /// <para><b>Framed, so that two different sets of sources cannot hash alike.</b> Each entry
    /// contributes its path length, its path, its content length and its content — concatenating
    /// them raw would make <c>"a" + "bc"</c> and <c>"ab" + "c"</c> the same bytes, and a collision
    /// here would let the generator accept sources the definition was not written against.</para>
    ///
    /// <para>Content is already LF-normalised. It has to be: the writer normalises every file it
    /// writes before it hashes what it wrote, so a hash taken over CRLF would disagree with the file
    /// that lands, and a CRLF checkout would build a different application from an LF one.</para>
    /// </summary>
    private static string Hash(SortedDictionary<string, string> files)
    {
        var bytes = new List<byte>();

        foreach (var (path, content) in files)
        {
            var p = Encoding.UTF8.GetBytes(path);
            var c = Encoding.UTF8.GetBytes(content);

            bytes.AddRange(Length32(p.Length));
            bytes.AddRange(p);
            bytes.AddRange(Length64(c.LongLength));
            bytes.AddRange(c);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData([.. bytes]));
    }

    private static byte[] Length32(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    private static byte[] Length64(long value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }
}
