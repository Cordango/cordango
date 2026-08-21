// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Cordango.AccessTokens;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Remote;

/// <summary>One instance the user has logged into.</summary>
/// <param name="Origin">Scheme, host and port — the key this is stored under.</param>
/// <param name="Token">The raw access token, as pasted.</param>
/// <param name="TenantId">The workspace the SERVER said the token belongs to at login. Recorded so
/// `cordango whoami` can answer offline; never trusted in place of asking again.</param>
/// <param name="User">Who the token acts as, for the same reason.</param>
public sealed record InstanceLogin(
    string Origin,
    string Token,
    string TenantId,
    string User,
    DateTimeOffset LoggedInAt);

/// <summary>
/// Where <c>cordango login</c> puts the credential, and how a workspace remembers which instance it
/// publishes to.
///
/// <para><b>Never in the repository.</b> Two separate reasons, and both matter. A token in the
/// checkout gets committed — that is not a risk, it is an eventual certainty. And a workspace is
/// cloned by teammates who each have their OWN credential and may publish to a different instance
/// entirely; a binding baked into <c>cordango.yaml</c> would make one person's target everybody's.</para>
///
/// <para><b>Keyed by workspace id, not by path.</b> <see cref="WorkspaceFile.WorkspaceId"/> is
/// committed and stable, so moving or re-cloning a checkout keeps its binding. That is the reason
/// the id was written into the manifest before anything read it.</para>
/// </summary>
public sealed class Credentials
{
    public const int FormatVersion = 1;

    private readonly JsonObject _doc;

    private Credentials(JsonObject doc) => _doc = doc;

    /// <summary>Overrides the directory holding <see cref="Path"/>. For a CI runner with no home
    /// directory, a container that mounts its config elsewhere, and the tests — which must never
    /// write a credential into the developer's real profile.</summary>
    public const string DirectoryVariable = "CORDANGO_CONFIG_DIR";

    /// <summary>The same override under the name it had while the command was called <c>cord</c>.
    /// Honoured when the current one is unset, so a CI job that exports the old variable keeps
    /// working instead of silently writing somewhere else.</summary>
    public const string LegacyDirectoryVariable = "CORD_CONFIG_DIR";

    private const string DirectoryName = "cordango";
    private const string LegacyDirectoryName = "cord";
    private const string FileName = "credentials.json";

    /// <summary>
    /// The file. Under the platform's per-user application-data directory, which
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> resolves to <c>%APPDATA%</c> on
    /// Windows and <c>$XDG_CONFIG_HOME</c> (or <c>~/.config</c>) elsewhere — one call, three correct
    /// answers.
    /// </summary>
    public static string Path => System.IO.Path.Combine(ConfigDirectory(DirectoryName), FileName);

    /// <summary>Where a login written before the rename still sits. Read on the way in, never
    /// written: the first <see cref="Flush"/> lands at <see cref="Path"/> and the old file is left
    /// exactly as it is. Deleting it would be the tidier gesture and the wrong one — it is somebody
    /// else's credential store if an older build is still installed, and orphaning a file is
    /// cheaper than logging them out of it.</summary>
    private static string LegacyPath =>
        System.IO.Path.Combine(ConfigDirectory(LegacyDirectoryName), FileName);

    private static string ConfigDirectory(string name)
    {
        foreach (var variable in new[] { DirectoryVariable, LegacyDirectoryVariable })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } overridden)
                return overridden;

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            name);
    }

    public static Credentials Load()
    {
        try
        {
            // Current location first. A machine holding both is one that has run either side of the
            // rename, and the current file is the one this build wrote.
            foreach (var candidate in new[] { Path, LegacyPath })
            {
                if (!File.Exists(candidate)) continue;
                if (JsonNode.Parse(File.ReadAllText(candidate)) is not JsonObject doc) continue;

                // A file from a newer cordango is left alone rather than rewritten in this version's
                // shape — overwriting it would silently log the user out of instances this build
                // cannot see.
                if ((int?)doc["version"] == FormatVersion) return new Credentials(doc);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException)
        {
            // An unreadable credential file means "not logged in", which is recoverable by logging
            // in again. Failing the command instead would strand somebody behind a file they
            // probably cannot read either.
        }

        return new Credentials(new JsonObject
        {
            ["version"] = FormatVersion,
            ["instances"] = new JsonObject(),
            ["workspaces"] = new JsonObject(),
        });
    }

    private JsonObject Instances => _doc["instances"] as JsonObject ?? Set("instances");
    private JsonObject Workspaces => _doc["workspaces"] as JsonObject ?? Set("workspaces");

    private JsonObject Set(string name)
    {
        var fresh = new JsonObject();
        _doc[name] = fresh;
        return fresh;
    }

    public InstanceLogin? Find(string origin)
    {
        var key = CordAccessToken.NormalizeOrigin(origin);
        if (Instances[key] is not JsonObject row) return null;

        return new InstanceLogin(
            key,
            (string?)row["token"] ?? "",
            (string?)row["tenantId"] ?? "",
            (string?)row["user"] ?? "",
            DateTimeOffset.TryParse((string?)row["loggedInAt"], out var at) ? at : default);
    }

    public IEnumerable<string> Origins => Instances.Select(kv => kv.Key);

    public void Save(InstanceLogin login)
    {
        Instances[login.Origin] = new JsonObject
        {
            ["token"] = login.Token,
            ["tenantId"] = login.TenantId,
            ["user"] = login.User,
            ["loggedInAt"] = login.LoggedInAt.ToString("O"),
        };
    }

    /// <summary>Forget one instance, or every instance when <paramref name="origin"/> is null.</summary>
    /// <returns>How many logins were dropped.</returns>
    public int Forget(string? origin)
    {
        if (origin is null)
        {
            var all = Instances.Count;
            Set("instances");
            Set("workspaces"); // a binding to an instance we have no credential for is just noise
            return all;
        }

        var key = CordAccessToken.NormalizeOrigin(origin);
        if (!Instances.Remove(key)) return 0;

        foreach (var bound in Workspaces.Where(kv => (string?)kv.Value == key).Select(kv => kv.Key).ToList())
            Workspaces.Remove(bound);

        return 1;
    }

    /// <summary>Which instance this workspace publishes to, or null when it has never published.</summary>
    public string? InstanceFor(string workspaceId) =>
        workspaceId.Length == 0 ? null : (string?)Workspaces[workspaceId];

    public void Bind(string workspaceId, string origin)
    {
        if (workspaceId.Length == 0) return;
        Workspaces[workspaceId] = CordAccessToken.NormalizeOrigin(origin);
    }

    /// <summary>
    /// Write the file back, readable by this user alone.
    ///
    /// <para>The mode is set BEFORE the content is written, so the token is never briefly on disk
    /// under the default umask. On Windows the NTFS ACL inherited from the per-user application-data
    /// directory already restricts it, and <see cref="File.SetUnixFileMode"/> is a no-op there.</para>
    /// </summary>
    public void Flush()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        if (!File.Exists(Path)) File.Create(Path).Dispose();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.WriteAllText(Path, _doc.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }) + "\n");
    }
}
