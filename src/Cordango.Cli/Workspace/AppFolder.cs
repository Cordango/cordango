// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;
using Cordango.Cord;

namespace Cordango.Cli.Workspace;

/// <summary>
/// One app's source files, loaded into the model and written back.
/// </summary>
/// <param name="Path">Repo-relative, forward-slashed — <c>apps/expense-approval</c>. What
/// <c>cordango.yaml</c> registers and what every message names.</param>
/// <param name="App">The assembled model. Null when the files could not be read as one.</param>
/// <param name="Files">What was on disk, app-relative path (without the extension) → exact bytes.
/// Kept so a write can skip files that already say the right thing, which is what makes a
/// one-aggregate change produce a one-file diff.</param>
/// <param name="LegacyPaths">The subset of <paramref name="Files"/> that was found under the
/// pre-rename <c>.cord.yaml</c> extension. A write treats these as changed even when the content
/// matches byte for byte: the filename is part of canonical form, so a workspace that never edits
/// an aggregate would otherwise keep the old name forever and <c>fmt</c> would keep calling it
/// canonical.</param>
public sealed record LoadedApp(
    string Path,
    string Directory,
    CordApp? App,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Problems,
    IReadOnlySet<string>? LegacyPaths = null)
{
    /// <summary>The app assembled AND every file was read. Both, because a partial read produces a
    /// perfectly usable model of an app that is not the one on disk.</summary>
    [MemberNotNullWhen(true, nameof(App))]
    public bool Ok => App is not null && Problems.Count == 0;

    /// <summary>The app key, from the identity file. Authoritative — the folder name is descriptive
    /// (CordyOSS §3.6), so moving a directory cannot rename an app.</summary>
    public string Key => App?.Key ?? System.IO.Path.GetFileName(Path);
}

/// <summary>Which files a write touches, app-relative in <see cref="WrittenPaths"/> and
/// repo-relative in <see cref="Written"/>.</summary>
public sealed record FileDiff(
    string AppPath,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> DeletedPaths)
{
    public IEnumerable<string> Written => WrittenPaths.Select(p => $"{AppPath}/{p}{AppFolder.Extension}");
    public IEnumerable<string> Deleted => DeletedPaths.Select(p => $"{AppPath}/{p}{AppFolder.Extension}");
    public bool Empty => WrittenPaths.Count == 0 && DeletedPaths.Count == 0;
}

/// <summary>
/// The filesystem half of the workspace: <c>.cordango.yaml</c> files in, <see cref="CordApp"/> out, and
/// back.
///
/// <para><b>Three layers, and this one owns only the outer.</b>
/// <see cref="CordSource"/> owns the FORMAT — which aggregates get files, what they are called and
/// what goes in them — and is pure, so it travels with the portable library.
/// <see cref="Yaml"/> owns the SYNTAX. This owns the filesystem: which bytes are on disk, what
/// changed, and writing it back without leaving half a file behind.</para>
/// </summary>
public static class AppFolder
{
    /// <summary>Appended to every <see cref="CordSourceFile.Path"/>. The format does not know about
    /// it — swapping the syntax is a change here and in <see cref="Yaml"/>, nowhere else.</summary>
    public const string Extension = ".cordango.yaml";

    /// <summary>What app source was called while the command was still called <c>cord</c>.
    ///
    /// <para>Read, never written. Files are keyed by their aggregate path rather than by their
    /// filename, so a pre-rename workspace loads into exactly the same model and the first
    /// <see cref="Save"/> writes the aggregate under its current name and deletes the old file.
    /// Leaving both would mean one aggregate with two files on disk, and the next load would read
    /// whichever the filesystem enumerated last.</para></summary>
    public const string LegacyExtension = ".cord.yaml";

    /// <summary>
    /// Reads one app directory.
    ///
    /// <para>A problem is never thrown. Somebody editing files gets them wrong routinely, and the
    /// useful answer is "these three files are wrong" rather than one exception naming the first.</para>
    /// </summary>
    public static LoadedApp Load(string root, string appPath)
    {
        var directory = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(root, appPath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        if (!Directory.Exists(directory))
            return new LoadedApp(appPath, directory, null, new Dictionary<string, string>(),
                [$"{appPath}: registered in {WorkspaceFile.FileName} but the directory does not exist"]);

        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        var documents = new List<CordSourceFile>();
        var problems = new List<string>();
        var legacy = new HashSet<string>(StringComparer.Ordinal);

        // Current extension first, so that when both spellings of one aggregate exist the current
        // one is the value that survives into `contents`.
        var onDisk = Directory
            .EnumerateFiles(directory, "*" + LegacyExtension, SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*" + Extension, SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in onDisk)
        {
            // `.cordango/` is the BUILD directory and must never be read as source. It lives at the
            // workspace root so this cannot normally happen, but a nested one would turn yesterday's
            // build output into today's input. The pre-rename name is skipped for the same reason.
            var relative = System.IO.Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative.StartsWith(".cordango/", StringComparison.Ordinal)
                || relative.StartsWith(".cord/", StringComparison.Ordinal)) continue;

            var suffix = relative.EndsWith(Extension, StringComparison.Ordinal)
                ? Extension : LegacyExtension;
            var key = relative[..^suffix.Length];
            if (suffix == LegacyExtension) legacy.Add(key); else legacy.Remove(key);
            string text;
            try
            {
                text = File.ReadAllText(file, Yaml.Encoding);
            }
            catch (IOException ex)
            {
                problems.Add($"{appPath}/{relative}: unreadable ({ex.Message})");
                continue;
            }

            contents[key] = text;

            var (document, error) = Yaml.Read(text);
            if (document is null) problems.Add($"{appPath}/{relative}: {error}");
            else documents.Add(new CordSourceFile(key, document));
        }

        if (documents.Count == 0 && problems.Count == 0)
            return new LoadedApp(appPath, directory, null, contents,
                [$"{appPath}: no {Extension} files — an app needs at least "
                    + $"{CordSource.AppFile}{Extension}"]);

        var (app, sourceProblems) = CordSource.Read(documents);
        problems.AddRange(sourceProblems.Select(p => $"{appPath}/{p}"));

        return new LoadedApp(appPath, directory, problems.Count == 0 ? app : null, contents, problems, legacy);
    }

    /// <summary>
    /// Writes the app back as canonical files, touching only what changed.
    ///
    /// <para><b>Write the whole app, save the difference.</b> Tracking which aggregate an operation
    /// touched and writing only that file would be a second implementation of
    /// <see cref="CordSource"/>'s partition, free to disagree with it. Comparing content gets the
    /// same one-file diff and cannot drift, because the partition it relies on is the only one there
    /// is.</para>
    ///
    /// <para><b>Removal deletes.</b> An entity removed by an operation must lose its file, or the
    /// next load would resurrect it. Only <c>.cordango.yaml</c> files inside the app directory are ever
    /// deleted.</para>
    /// </summary>
    public static FileDiff Save(LoadedApp loaded, IReadOnlyList<CordSourceFile> source)
    {
        var (diff, rendered) = Compare(loaded, source);

        foreach (var path in diff.WrittenPaths)
        {
            AtomicFile.Write(Resolve(loaded.Directory, path, Extension), rendered[path]);
            // The aggregate now exists under its current name, so a pre-rename file for the same
            // aggregate is a duplicate, not a backup.
            Remove(Resolve(loaded.Directory, path, LegacyExtension));
        }

        foreach (var path in diff.DeletedPaths)
        {
            Remove(Resolve(loaded.Directory, path, Extension));
            Remove(Resolve(loaded.Directory, path, LegacyExtension));
        }

        if (diff.DeletedPaths.Count > 0) PruneEmptyDirectories(loaded.Directory);
        return diff;

        static string Resolve(string directory, string path, string extension) =>
            System.IO.Path.Combine(directory,
                (path + extension).Replace('/', System.IO.Path.DirectorySeparatorChar));

        // Deleting what is not there is the normal case for the legacy spelling, and the ordinary
        // case for the current one after a rename; neither is worth an exception.
        static void Remove(string file)
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    /// <summary>What <see cref="Save"/> WOULD do — same comparison, no side effects, so
    /// <c>--dry-run</c> reports exactly what the real thing writes rather than an approximation.</summary>
    public static FileDiff Diff(LoadedApp loaded, IReadOnlyList<CordSourceFile> source) =>
        Compare(loaded, source).Diff;

    /// <summary>The comparison both <see cref="Save"/> and <see cref="Diff"/> run, plus the rendered
    /// bytes so a write does not serialize every document twice.</summary>
    private static (FileDiff Diff, Dictionary<string, string> Rendered) Compare(
        LoadedApp loaded, IReadOnlyList<CordSourceFile> source)
    {
        var rendered = source.ToDictionary(f => f.Path, f => Yaml.Write(f.Document), StringComparer.Ordinal);

        var legacy = loaded.LegacyPaths ?? new HashSet<string>(StringComparer.Ordinal);

        var written = rendered
            .Where(kv => !loaded.Files.TryGetValue(kv.Key, out var existing)
                      || !string.Equals(existing, kv.Value, StringComparison.Ordinal)
                      // Same bytes, wrong filename: still a write, so  migrates a pre-rename
                      // workspace instead of calling it canonical forever.
                      || legacy.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var deleted = loaded.Files.Keys
            .Where(p => !rendered.ContainsKey(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return (new FileDiff(loaded.Path, written, deleted), rendered);
    }

    /// <summary>An <c>entities/</c> folder left behind after its last entity was removed reads as an
    /// app that still has entities. Only empty directories, and only under the app.</summary>
    private static void PruneEmptyDirectories(string root)
    {
        foreach (var dir in Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
    }

    /// <summary>A Cord error as one line a person can act on.
    ///
    /// <para><see cref="CordError.ToString"/> already carries the operation index, the Cord path and
    /// the ambiguous-join candidates — the three things that turn "this was refused" into "fix line
    /// 4". Reformatting would drop whichever the next reader forgot about.</para>
    /// </summary>
    public static string Describe(CordError error) => error.ToString();
}
