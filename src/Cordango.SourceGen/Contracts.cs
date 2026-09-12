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

/// <summary>
/// The custom code an application carries, as CONTENT rather than as a path.
///
/// <para><b>Normalised and hashed once, by whoever read the disk.</b> The definition records the
/// hash; this carries the bytes it was taken over. A generator verifies the two agree before it
/// writes anything, which is what makes "the definition says code A" and "the application contains
/// code A" the same statement rather than two hopes.</para>
///
/// <para>Content is LF-normalised, and keys are forward-slashed paths relative to
/// <c>custom/&lt;language&gt;/</c>. Both matter: the writer normalises line endings before it
/// hashes what it wrote, so a hash taken over CRLF would disagree with the file that lands.</para>
/// </summary>
public sealed record CustomSourceBundle(
    string Language, string Hash, IReadOnlyDictionary<string, string> Files);

/// <summary>What the CLI asks a generator to produce.</summary>
/// <param name="Options">Target-specific switches (seed value, locales, partial-UI). Deliberately
/// untyped: the SDK does not get a vote on what a generator needs to be told.</param>
/// <param name="CustomSources">The bodies behind <c>definition.custom</c>, when there are any.
/// TYPED and separate from <paramref name="Options"/> on purpose: this is the one input a generator
/// must VERIFY rather than merely read, and burying it in an untyped bag is how it would end up
/// unverified. Trailing and optional so that every existing call site still compiles.</param>
public sealed record GenerateRequest(
    CompiledAppArtifact App, JsonObject Options, CustomSourceBundle? CustomSources = null)
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

/// <summary>
/// One entity, as the scanner needs to know it.
///
/// <para>The KEY and nothing else. A scanner that needs the generated class name works it out with
/// the same naming rules its emitters use — it is the component that owns them. Handing it a name
/// computed somewhere else would put the mapping in two places, and the second one would be a guess
/// about the first.</para>
/// </summary>
public sealed record CustomEntity(string Key);

/// <summary>
/// What a custom-code scanner is given: semantic facts and the sources, never a filesystem.
///
/// <para>Deliberately free of anything target-shaped. The scan happens before a build target is
/// chosen — <c>cordango check</c> has no target at all, by design, and still has to type-check an
/// expression that calls custom code. So what arrives here is what the definition knows.</para>
/// </summary>
public sealed record CustomCodeContext(
    string AppKey,
    string AppName,
    IReadOnlyList<CustomEntity> Entities,
    IReadOnlyDictionary<string, string> Sources);

/// <summary>
/// What a scan found: the contract, and two separate lists.
///
/// <para><b>Errors and warnings are different channels because they reach different places.</b> An
/// error means the workspace does not hold together and the app will not load; a warning means the
/// code does something questionable and the build continues. Merged into one list, a note about an
/// <c>HttpClient</c> would read to the CLI as "your app does not hold together", which is both
/// wrong and unfixable-looking.</para>
/// </summary>
public sealed record CustomScanResult(
    JsonObject Metadata,
    IReadOnlyList<Diagnostic> Errors,
    IReadOnlyList<Diagnostic> Warnings)
{
    public static CustomScanResult Empty => new(new JsonObject(), [], []);
}

/// <summary>
/// Reads custom source in ONE language and answers with the contract the compiler can type-check
/// expressions against.
///
/// <para>Implemented by the generator that owns the language, because the language is what the two
/// have in common: the thing that knows how to read C# is the thing that knows how to emit it. A
/// target that generates another language implements its own, and neither pays for the other's
/// parser.</para>
///
/// <para>It reads no files and runs no compiler. <c>cordango check</c> is offline and must stay
/// that way, so a signature is read out of the text of a declaration rather than out of a build.
/// The backstop is that the generated call site is compiled later by the real compiler, which has
/// the final word on whether the signature was read correctly.</para>
/// </summary>
public interface ICustomCodeScanner
{
    /// <summary>The <c>custom/&lt;language&gt;/</c> directory this reads, e.g. <c>dotnet</c>.</summary>
    string Language { get; }

    /// <summary>The extension its sources carry, e.g. <c>.cs</c>.</summary>
    string SourceExtension { get; }

    CustomScanResult Scan(CustomCodeContext context);
}
