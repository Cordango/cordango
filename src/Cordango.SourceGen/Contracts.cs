// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.SourceGen;

/// <summary>Which compiler produced an artifact, recorded so a generated repository can say what
/// built it years later.</summary>
public sealed record CompilerInfo(string Id, string Version);

/// <summary>
/// ONE canonical input, and the reason it is one thing rather than two.
///
/// <para>A generator needs the definition (what the author wrote) and the manifest (what the
/// compiler derived from it: canonicalised processes, synthesised commands, base fields, default
/// views). Handing those over as two parameters invites a caller to supply a definition and a
/// manifest that do not describe the same application — which is exactly the "which representation
/// is truth?" problem the App Definition exists to avoid, reintroduced at the last possible
/// moment.</para>
///
/// <para>So they travel as a pair, produced together by one compile. This is not a new intermediate
/// representation: every field here already existed as an output of the existing pipeline. The type
/// only makes a mismatched pair impossible to express.</para>
/// </summary>
/// <param name="Definition">The App Definition after deterministic fills — the artifact
/// <paramref name="DefinitionHash"/> covers.</param>
/// <param name="Manifest">What the compiler derived. A generator reads process/command/field facts
/// from HERE rather than re-deriving them, so it cannot disagree with the runtime about what the
/// definition meant.</param>
public sealed record CompiledAppArtifact(
    JsonObject Definition,
    JsonObject Manifest,
    string DefinitionHash,
    CompilerInfo Compiler);

/// <summary>What the CLI asks a generator to produce.</summary>
/// <param name="Options">Target-specific switches (seed value, locales, partial-UI). Deliberately
/// untyped: the SDK does not get a vote on what a generator needs to be told.</param>
public sealed record GenerateRequest(CompiledAppArtifact App, JsonObject Options)
{
    public static GenerateRequest For(CompiledAppArtifact app) => new(app, new JsonObject());

    /// <summary>A string option, or null. Reading options through one accessor keeps a generator
    /// from inventing three spellings of "is it there".</summary>
    public string? Option(string name) =>
        Options[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public bool Flag(string name) =>
        Options[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>
/// One file a generator wants written.
///
/// <para><paramref name="RelativePath"/> is forward-slashed and relative to the output root. It is
/// NOT a path the generator writes to — the runner owns the filesystem, so a generator cannot
/// escape the output directory, cannot leave a half-written tree behind, and cannot make two runs
/// differ by writing in a different order.</para>
/// </summary>
public sealed record GeneratedFile(string RelativePath, string Content);

/// <summary>Something a target has to say about a definition it was asked to build.</summary>
/// <param name="Code">A stable <c>CORD21xx</c> identifier. Codes exist so a person can search for
/// one and a script can branch on one without reading English.</param>
/// <param name="JsonPath">Where in the definition, e.g. <c>$.entities[2].fields[7]</c>. A capability
/// message without a location is a puzzle rather than a report.</param>
public sealed record Diagnostic(string Code, string Message, string? JsonPath = null)
{
    public override string ToString() =>
        JsonPath is null ? $"{Code}: {Message}" : $"{Code}: {Message} ({JsonPath})";
}

/// <summary>What a generator answered.</summary>
/// <param name="Errors">Non-empty means nothing is written. A generator reports EVERY reason it can
/// find rather than the first, because a person fixing an app wants the list.</param>
public sealed record GenerateResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<Diagnostic> Warnings,
    IReadOnlyList<Diagnostic> Errors)
{
    public bool Ok => Errors.Count == 0;

    public static GenerateResult Failed(params Diagnostic[] errors) => new([], [], errors);

    public static GenerateResult Produced(
        IReadOnlyList<GeneratedFile> files, IReadOnlyList<Diagnostic>? warnings = null) =>
        new(files, warnings ?? [], []);
}

/// <summary>
/// A source generator for one target stack.
///
/// <para>It receives a compiled application and returns files. It does not know about YAML, about
/// workspaces, about a Cordango server, or about where its output will land — the CLI owns all of
/// that. The narrowness is the point: it is the same surface an out-of-process generator written in
/// another language gets over stdin and stdout.</para>
/// </summary>
public interface IAppSourceGenerator
{
    /// <summary>Stable identifier, e.g. <c>dotnet-vue</c>. This is what <c>--target</c> names.</summary>
    string Id { get; }

    /// <summary>Semantic version of the GENERATOR, recorded in the build metadata. Two runs of the
    /// same version on the same definition must produce the same bytes.</summary>
    string Version { get; }

    /// <summary>What this target can and cannot express. Answerable without generating anything, so
    /// <c>cordango check --target</c> costs nothing.</summary>
    GeneratorCapabilities Capabilities { get; }

    GenerateResult Generate(GenerateRequest request);
}
