// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.SourceGen;

/// <summary>What a write did, or would have done.</summary>
public sealed record WriteReport(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<Diagnostic> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// The runner owns the filesystem. A generator returns files; only this writes them.
///
/// <para><b>Why a generator is not handed a directory.</b> Three properties come free once it is
/// not: a generator cannot escape the output root (<c>../../.ssh/authorized_keys</c> is a string
/// this rejects, not a path it opens), it cannot leave half a tree behind when it throws
/// mid-emission, and it cannot make two runs differ by iterating a dictionary in a different order.
/// The third is the one that would be discovered last and cost the most: determinism is the whole
/// promise, and it is not a property a generator can hold on its own.</para>
///
/// <para><b>Regeneration deletes only what it wrote.</b> The previous <c>cordango.build.json</c>
/// lists the files this generator owns, so a rebuild removes exactly those and leaves everything
/// else — a test somebody added, a README they rewrote, their <c>.git</c> — untouched. A directory
/// with no build metadata is refused rather than merged into, because "generate into an existing
/// project" is a different feature with different rules and guessing which one was meant is how
/// somebody loses work.</para>
/// </summary>
public static class GeneratedFileWriter
{
    /// <summary>UTF-8 with NO byte-order mark. A BOM would make the same content differ byte-wise
    /// from what every other tool writes, and would sit at the top of files a compiler reads.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static WriteReport Write(
        string outputRoot, GenerateResult result, BuildMetadataDraft draft, bool dryRun = false)
    {
        var root = Path.GetFullPath(outputRoot);

        // Ordinal, not culture-aware: a Turkish locale sorts 'I' elsewhere, and a file order that
        // depends on where the machine is would break byte-comparison across a team.
        //
        // Line endings are normalised HERE, once, before anything hashes or writes. Doing it at
        // write time instead left the metadata recording a hash of what the generator handed over
        // rather than of what landed on disk: a generator emitting Windows endings produced
        // byte-identical FILES and a different manifest from one emitting Unix endings, which is
        // the determinism claim failing inside the artifact that documents it.
        var files = result.Files
            .Select(f => f with { Content = f.Content.Replace("\r\n", "\n") })
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        var rejected = files.Select(f => Reject(f.RelativePath)).OfType<Diagnostic>().ToList();
        if (rejected.Count > 0) return new WriteReport([], [], rejected);

        var duplicates = files.GroupBy(f => f.RelativePath, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new Diagnostic("CORD2201", $"two files claim '{g.Key}'"))
            .ToList();
        if (duplicates.Count > 0) return new WriteReport([], [], duplicates);

        var metadataPath = Path.Combine(root, BuildMetadata.FileName);
        var stale = new List<string>();

        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!File.Exists(metadataPath))
                return new WriteReport([], [], [new Diagnostic("CORD2200",
                    $"'{root}' is not empty and has no {BuildMetadata.FileName}, so it is not "
                    + "output this generator produced. Choose an empty directory, or delete it "
                    + "yourself if you meant to replace it.")]);

            var owned = BuildMetadata.PreviousFiles(metadataPath);
            if (owned.Count == 0)
                return new WriteReport([], [], [new Diagnostic("CORD2200",
                    $"'{Path.Combine(root, BuildMetadata.FileName)}' could not be read, so there is "
                    + "no way to tell which files are this generator's to replace.")]);

            var keeping = files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
            stale = [.. owned.Where(p => !keeping.Contains(p)).OrderBy(p => p, StringComparer.Ordinal)];
        }

        var metadata = new BuildMetadata(
            draft.DefinitionHash, draft.Compiler, draft.GeneratorId, draft.GeneratorVersion,
            draft.Unsupported, [.. files.Select(GeneratedFileRecord.Of)]);

        if (dryRun)
            return new WriteReport([.. files.Select(f => f.RelativePath)], stale, []);

        foreach (var relative in stale)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) File.Delete(path);
        }

        Directory.CreateDirectory(root);
        foreach (var file in files) WriteOne(root, file.RelativePath, file.Content);

        // Metadata last: a run interrupted before this point leaves a directory whose manifest still
        // describes the PREVIOUS build, which is recoverable. Writing it first would leave a manifest
        // describing files that were never written.
        WriteOne(root, BuildMetadata.FileName, metadata.ToJson().ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

        PruneEmptyDirectories(root);
        return new WriteReport([.. files.Select(f => f.RelativePath)], stale, []);
    }

    /// <summary>
    /// Everything a relative path is not allowed to be.
    ///
    /// <para>Checked as a string AND re-checked as a resolved path. The string rules catch the
    /// obvious shapes and give a message naming the actual problem; the resolved check is what
    /// actually holds, because path normalisation has more corner cases than a list of rules does —
    /// and being wrong here means a generator writing outside the directory the user named.</para>
    /// </summary>
    private static Diagnostic? Reject(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return new Diagnostic("CORD2202", "a generated file has no path");

        if (relative.Contains('\0', StringComparison.Ordinal))
            return new Diagnostic("CORD2202", "a generated path contains a null character");

        var normalised = relative.Replace('\\', '/');

        if (normalised.StartsWith('/') || Path.IsPathRooted(relative) || relative.Contains(':'))
            return new Diagnostic("CORD2202",
                $"'{relative}' is absolute. Generated paths are relative to the output directory.");

        if (normalised.Split('/').Any(segment => segment is ".."))
            return new Diagnostic("CORD2202",
                $"'{relative}' climbs out of the output directory.");

        var root = Path.GetFullPath("/cordango-write-probe");
        var resolved = Path.GetFullPath(Path.Combine(root, normalised.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new Diagnostic("CORD2202",
                $"'{relative}' does not resolve inside the output directory.");

        return null;
    }

    /// <summary>Write-temp-then-rename, so an interrupted write leaves either the old file or the
    /// new one and never half of either. Content arrives already normalised — see the note in
    /// <see cref="Write"/> about why that has to happen before anything hashes it.</summary>
    private static void WriteOne(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp" + Environment.ProcessId;
        File.WriteAllText(temp, content, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    private static void PruneEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}

/// <summary>The metadata facts known before the file list exists. The writer completes it with the
/// hashes, so nothing else has to remember to.</summary>
public sealed record BuildMetadataDraft(
    string DefinitionHash,
    CompilerInfo Compiler,
    string GeneratorId,
    string GeneratorVersion,
    IReadOnlyList<Diagnostic> Unsupported);
