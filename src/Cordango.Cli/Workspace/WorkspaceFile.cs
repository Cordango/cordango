// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cli.Workspace;

/// <summary>
/// The root manifest — one Cordango installation, expressed as source (CordyOSS §3.1).
/// </summary>
/// <param name="Root">Absolute path to the directory holding <c>cordango.yaml</c>.</param>
/// <param name="WorkspaceId">Generated once by <c>cordango new</c> and committed. It anchors local
/// runtime data to the workspace rather than to its absolute path, so moving a checkout does not
/// silently produce an empty database. Nothing in this slice reads it; it is written now because
/// adding it later would mean every existing workspace has no identity.</param>
/// <param name="Name">Display name, defaulted from the directory name at creation.</param>
/// <param name="Apps">Repo-relative app directories, forward-slashed. EXPLICIT: a stray folder under
/// <c>apps/</c> is reported by <c>doctor</c> and never installed merely because it exists.</param>
/// <param name="Build">How this workspace builds, or null when nobody has said yet. Optional on the
/// constructor so that adding it did not have to touch every caller — and null-by-default is the
/// right default anyway: a workspace that has never been configured must be distinguishable from one
/// configured to the same values on purpose.</param>
public sealed record WorkspaceFile(
    string Root,
    string WorkspaceId,
    string Name,
    string Runtime,
    IReadOnlyList<string> Apps,
    BuildConfig? Build = null)
{
    public const string FileName = "cordango.yaml";

    /// <summary>What the root manifest was called while the command was still called <c>cord</c>.
    ///
    /// <para>Read, never written. A workspace created before the rename still opens, and the first
    /// <see cref="Save"/> migrates it: the new file is written and this one is deleted, so a
    /// checkout never ends up with two manifests where the stale one wins on the next
    /// <see cref="Find"/>. Deleting rather than leaving it is the whole point — a second manifest is
    /// not a backup, it is a second answer to "which apps are installed".</para></summary>
    public const string LegacyFileName = "cord.yaml";

    public const int FormatVersion = 1;

    /// <summary>The runtime range a fresh workspace declares. Pre-1.0 and deliberately narrow —
    /// nothing about this format is promised across a minor bump yet.</summary>
    public const string DefaultRuntime = ">=0.1 <0.2";

    public string Path => System.IO.Path.Combine(Root, FileName);

    /// <summary>
    /// Finds the workspace by walking UP from <paramref name="from"/>, so every command works from
    /// any descendant directory — which is what lets an agent run `cordango check` from inside
    /// `apps/expenses/entities/` without knowing where the root is.
    /// </summary>
    public static WorkspaceFile? Find(string from, out string? problem)
    {
        problem = null;
        var dir = new DirectoryInfo(System.IO.Path.GetFullPath(from));

        while (dir is not null)
        {
            // Current name first: a directory holding both is one mid-migration, and the new file is
            // the authority there.
            var candidate = System.IO.Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return Read(candidate, out problem);

            var legacy = System.IO.Path.Combine(dir.FullName, LegacyFileName);
            if (File.Exists(legacy)) return Read(legacy, out problem);

            dir = dir.Parent;
        }

        return null;
    }

    public static WorkspaceFile? Read(string path, out string? problem)
    {
        problem = null;
        JsonObject doc;
        try
        {
            var (parsed, error) = Yaml.Read(File.ReadAllText(path, Yaml.Encoding));
            if (parsed is null)
            {
                problem = $"{path}: {error}";
                return null;
            }
            doc = parsed;
        }
        catch (IOException ex)
        {
            problem = $"{path}: unreadable ({ex.Message})";
            return null;
        }

        // An unknown format version is refused rather than tolerated. A newer workspace opened by an
        // older CLI would parse — the shape barely changes — and then be materialized back WITHOUT
        // whatever the newer version added, which is data loss disguised as a successful command.
        // Read as long, not int: YAML has one integer type and the reader produces the widest, so
        // an `(int?)` cast throws on a document that is perfectly well formed.
        var format = doc["formatVersion"] is JsonValue v && v.TryGetValue<long>(out var declared)
            ? declared : 0;
        if (format != FormatVersion)
        {
            problem = $"{path}: formatVersion {format} — this cordango understands {FormatVersion}. "
                + "Upgrade cordango, or check out a matching revision of this workspace.";
            return null;
        }

        var apps = (doc["apps"] as JsonArray ?? [])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .OfType<string>()
            .ToList();

        return new WorkspaceFile(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
            (string?)doc["workspaceId"] ?? "",
            (string?)doc["name"] ?? "",
            (string?)doc["runtime"] ?? DefaultRuntime,
            apps,
            BuildConfig.Read(doc[BuildConfig.Key]));
    }

    public void Save()
    {
        var doc = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["workspaceId"] = WorkspaceId,
            ["name"] = Name,
            ["runtime"] = Runtime,
            ["coreApps"] = "default",
        };

        // Above the app list rather than below it: `apps` grows without limit, and a setting written
        // after it would be off the bottom of the screen in any workspace worth having settings for.
        if (Build is not null) doc[BuildConfig.Key] = Build.ToDocument();

        doc["apps"] = new JsonArray([.. Apps.Select(a => (JsonNode)a!)]);

        AtomicFile.Write(Path, Yaml.Write(doc));

        // Migrate in place: the manifest has just been written under its current name, so a
        // leftover pre-rename one is now a stale duplicate rather than history.
        var legacy = System.IO.Path.Combine(Root, LegacyFileName);
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    /// <summary>Absolute directory for one registered app path.</summary>
    public string DirectoryOf(string appPath) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, appPath.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    /// <summary>
    /// A workspace id: ULID-shaped so it sorts by creation and carries no machine identity.
    ///
    /// <para>Deliberately NOT a GUID — this string ends up in a filesystem path under the user's
    /// application-data directory, and Crockford base32 is case-insensitive-safe on the two platforms
    /// whose filesystems are, while a GUID's braces and hyphens are noise there.</para>
    /// </summary>
    public static string NewId(DateTimeOffset now, Func<int, byte[]> randomBytes)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        var time = now.ToUnixTimeMilliseconds();
        var chars = new char[26];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(time & 31)];
            time >>= 5;
        }

        var random = randomBytes(16);
        for (var i = 0; i < 16; i++) chars[10 + i] = Alphabet[random[i] & 31];

        return new string(chars);
    }
}
