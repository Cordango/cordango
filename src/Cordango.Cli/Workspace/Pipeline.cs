// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Compile;
using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.Compile;

namespace Cordango.Cli.Workspace;

/// <summary>
/// What the full pipeline said about one app.
/// </summary>
/// <param name="Coherent">Gate-clean and compiling. The bar an app must clear for its source to be
/// worth keeping — see <see cref="CordTransaction"/>'s two thresholds.</param>
/// <param name="Valid">Additionally a FINISHED application: it has somewhere to land, something to
/// create. A domain authored before its screens is coherent and not valid, and that is a normal
/// state during authoring rather than an error.</param>
/// <param name="Fills">Holes the deterministic finalizer closed. Reported, never silent — a fill
/// means something was left out, which is worth seeing even on a green run.</param>
public sealed record CheckReport(
    string AppKey,
    string AppPath,
    bool Coherent,
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Fills,
    JsonNode? Definition,
    JsonObject? Manifest,
    string? DefinitionHashHex)
{
    /// <summary>Typed remarks that are neither refusals nor fills — see <see cref="DefinitionNote"/>.
    /// Not positional, so nothing that already builds a report has to change.</summary>
    public IReadOnlyList<DefinitionNote> Notes { get; init; } = [];

    /// <summary>The app's public surface, when it compiled to one. Computed with the manifest rather
    /// than at write time, so <c>check</c> and <c>discover</c> answer from the same projection the
    /// build writes out.</summary>
    public JsonObject? Contract { get; init; }

    public JsonObject ToJson() => new()
    {
        ["app"] = AppKey,
        ["path"] = AppPath,
        ["coherent"] = Coherent,
        ["valid"] = Valid,
        ["definitionHash"] = DefinitionHashHex,
        ["errors"] = new JsonArray([.. Errors.Select(e => (JsonNode)e!)]),
        ["fills"] = new JsonArray([.. Fills.Select(f => (JsonNode)f!)]),
        ["notes"] = new JsonArray([.. Notes.Select(n => (JsonNode)n.ToJson())]),
    };
}

/// <summary>
/// Semantic source → App Definition → verdict, in the one call the hosted product also makes.
///
/// <para><b>Why <see cref="CandidateValidator"/> and not a gate call.</b> The gate answers "is this
/// document legal". It does not repair a double-encoded section, stamp the schema version, apply the
/// create-path floor, compile a manifest, or distinguish unfinished from broken. Every one of those
/// has burned this codebase at least once, and re-deriving the sequence here would put a second
/// opinion about correctness in the product — the exact thing <c>CordError</c>'s rule 1 exists to
/// prevent one layer down.</para>
///
/// <para><b>Nothing here spends a token or opens a socket.</b> That is the non-negotiable gate from
/// CordyOSS §14: <c>check</c> and <c>build</c> must never need a model or a database.</para>
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// The build timestamp stamped into every manifest.
    ///
    /// <para><b>Fixed, so that equal source produces equal artifacts.</b> "Equal workspace source and
    /// tool versions produce equal AppDefinition and manifest hashes" is an acceptance criterion, and
    /// a wall-clock timestamp in the manifest breaks it on the second run of the same build. The real
    /// build time is recorded in <c>diagnostics.json</c>, which is metadata about the build rather
    /// than part of it.</para>
    ///
    /// <para>When <c>cordango run</c> needs a genuine build time in a served manifest, that becomes a
    /// deliberate decision about what the runtime reads — not an accident of using
    /// <c>UtcNow</c> here.</para>
    /// </summary>
    public static readonly DateTimeOffset DeterministicBuiltAt = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Check what the files say.
    ///
    /// <para><b>A load problem fails the check even when a model still assembled.</b>
    /// <see cref="CordFiles.Join"/> reports per file and DROPS what it refuses — an operation sitting
    /// in the wrong aggregate's file, a duplicate path, unreadable bytes. So an app carrying a
    /// problem loads as the app minus whatever was dropped, which would otherwise pass every
    /// downstream check while quietly not being what the files say. Silently ignoring part of the
    /// source is the one failure mode a file-backed workspace cannot have.</para>
    /// </summary>
    public static CheckReport Check(LoadedApp loaded)
    {
        if (loaded.App is null || loaded.Problems.Count > 0)
            return new CheckReport(loaded.Key, loaded.Path, false, false, loaded.Problems, [], null, null, null);

        return Check(loaded.App, loaded.Key, loaded.Path, CordPointerMap.Empty);
    }

    /// <param name="map">Rewrites App Definition pointers in the validator's errors back into Cord
    /// paths. Available only when a change was just prepared; a plain load has none, and a raw
    /// pointer is still better than a rewritten guess.</param>
    public static CheckReport Check(CordApp app, string appKey, string appPath, CordPointerMap map)
    {
        var lowered = CordLower.Lower(app);

        // appId: the app KEY, because in a workspace the key is the durable identity (CordyOSS §3.2)
        // and the manifest has to name something stable across machines. The hosted product passes a
        // row id here for the opposite reason — there, a model must not be able to name its own app.
        var outcome = CandidateValidator.Run(lowered, appKey, DeterministicBuiltAt);

        return new CheckReport(
            appKey,
            appPath,
            outcome.Coherent,
            outcome.Valid,
            [.. outcome.Errors.Select(map.Rewrite)],
            outcome.Fills,
            outcome.Definition,
            outcome.Manifest,
            outcome.Definition is { } d ? DefinitionHash.Of(d) : null)
        {
            Notes = outcome.Notes,
            Contract = outcome.Definition is JsonObject def && outcome.Manifest is { } m
                ? AppContract.Build(def, m)
                : null,
        };
    }
}
