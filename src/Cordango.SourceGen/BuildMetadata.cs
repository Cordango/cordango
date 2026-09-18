// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.SourceGen;

/// <summary>
/// <c>cordango.build.json</c>: what produced this repository, and what it knowingly left out.
///
/// <para><b>No timestamps, no random identifiers, no machine paths.</b> This file is written on
/// every generation and is the one most tempting place to record "generated at". A clock in here
/// would mean two runs of the same generator on the same definition differ, which would quietly end
/// the determinism claim in the file that exists to document it.</para>
///
/// <para><b>A partial build says so permanently.</b> Generating with unsupported screens demoted to
/// placeholders prints a warning once, in a terminal nobody will be looking at in six months. The
/// scar belongs in the artifact: <c>partial</c> plus every capability that was skipped, so a person
/// inheriting the repository can tell a deliberate subset from a finished build without having to
/// know the history.</para>
/// </summary>
/// <param name="Files">Every file written, with a hash. It is what makes a regeneration able to
/// clean up exactly what it put there last time and nothing else.</param>
public sealed record BuildMetadata(
    string DefinitionHash,
    CompilerInfo Compiler,
    string GeneratorId,
    string GeneratorVersion,
    IReadOnlyList<Diagnostic> Unsupported,
    IReadOnlyList<GeneratedFileRecord> Files)
{
    /// <summary>The name this lands under, and the marker that tells a regeneration it is looking at
    /// its own previous output rather than at somebody's unrelated directory.</summary>
    public const string FileName = "cordango.build.json";

    public const int ProtocolVersion = 1;

    /// <summary>
    /// Every name the build had to decide, as <c>kind:app:oldKey</c> → the name it was given.
    ///
    /// <para>Empty for the ordinary case. It fills when a workspace holds apps that chose the same
    /// key for something the merged deployment can only have one of — and it is read back on the next
    /// build so the answer does not change under a database that already has the table. This is the
    /// one entry here that is an INPUT as well as a record.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool Partial => Unsupported.Count > 0;

    public JsonObject ToJson() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["definitionHash"] = DefinitionHash,
        ["compiler"] = new JsonObject { ["id"] = Compiler.Id, ["version"] = Compiler.Version },
        ["generator"] = new JsonObject { ["id"] = GeneratorId, ["version"] = GeneratorVersion },
        ["partial"] = Partial,
        ["unsupportedCapabilities"] = new JsonArray([.. Unsupported.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code,
            ["feature"] = d.Message,
            ["path"] = d.JsonPath,
        })]),
        ["names"] = new JsonObject(Names
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => KeyValuePair.Create(n.Key, (JsonNode?)JsonValue.Create(n.Value)))),
        ["files"] = new JsonArray([.. Files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path,
            ["sha256"] = f.Sha256,
        })]),
    };

    /// <summary>
    /// Reads the names a previous build assigned, so this one can keep them.
    ///
    /// <para>Unreadable answers empty, which means "decide afresh". That is right for a first build
    /// and harmless for any other: the same workspace produces the same answers. It is deliberately
    /// NOT a failure — a missing map must not stop somebody building.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> PreviousNames(string path)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject doc) return names;
            if (doc["names"] is not JsonObject map) return names;

            foreach (var (key, value) in map)
                if (value is JsonValue v && v.TryGetValue<string>(out var assigned)) names[key] = assigned;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return names;
    }

    /// <summary>Reads the file list out of a previous build, so a regeneration knows what it owns.
    /// Anything unreadable answers empty: the caller then refuses to touch the directory, which is
    /// the safe direction.</summary>
    public static IReadOnlyList<string> PreviousFiles(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject doc) return [];
            if (doc["files"] is not JsonArray files) return [];
            return [.. files.OfType<JsonObject>()
                        .Select(f => f["path"]?.GetValue<string>())
                        .OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }
}

/// <param name="Sha256">Of the file's UTF-8 bytes as written, so a person can check a generated
/// repository against its own manifest without regenerating it.</param>
public sealed record GeneratedFileRecord(string Path, string Sha256)
{
    public static GeneratedFileRecord Of(GeneratedFile file) =>
        new(file.RelativePath,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))));
}
