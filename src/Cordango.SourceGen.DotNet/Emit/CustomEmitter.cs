// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNet.Emit;

/// <summary>
/// The application's own code, carried into the application.
///
/// <para><b>Copied, not referenced.</b> The generated repository builds with <c>docker compose up
/// --build</c>, whose build context is the output directory — a project reference pointing back into
/// the workspace would be outside that context and would not restore. Copying also keeps the promise
/// that a generated application is yours and can be moved: everything it compiles is inside it.</para>
///
/// <para>The workspace copy stays the one you edit. This one is generator-owned and is replaced on
/// every build, which the banner says so that nobody spends an afternoon editing a file whose
/// changes keep disappearing.</para>
/// </summary>
public static class CustomEmitter
{
    /// <summary>Where the copies land inside the generated application.</summary>
    public const string Directory = "api/Custom";

    /// <summary>
    /// Whether the bundle is the one the definition was compiled against.
    ///
    /// <para><b>The invariant this whole feature is built around.</b> The definition records a hash
    /// of the sources and this carries the sources; if they disagree, the application about to be
    /// written is not the one the definition describes, and its hash would vouch for code that never
    /// went into it. Checked before a single file is emitted.</para>
    ///
    /// <para>Language and hash are checked separately so the message is worth reading. A bundle in
    /// the wrong language is somebody building a .NET application from TypeScript sources, and being
    /// told the hashes differ would send them looking at the wrong thing entirely.</para>
    /// </summary>
    public static Diagnostic? Mismatch(AppModel app, CustomSourceBundle? bundle)
    {
        ArgumentNullException.ThrowIfNull(app);

        var declared = app.Manifest["custom"] as System.Text.Json.Nodes.JsonObject;
        if (declared is null && bundle is null) return null;

        if (declared is null)
            return new Diagnostic(CustomCodeCodes.Shape,
                "custom sources were supplied for an application whose definition declares none. "
                + "The definition is what says an application has custom code; sources that nothing "
                + "declares would be compiled in and called by nothing.");

        if (bundle is null)
            return new Diagnostic(CustomCodeCodes.Shape,
                "this definition declares custom code, and no sources came with it. A definition "
                + "carries the contract and a hash, never the bodies, so it cannot be built on its "
                + "own — build from the workspace the custom/ directory lives in.");

        var language = AppModel.Str(declared["language"]);
        if (!string.Equals(language, bundle.Language, StringComparison.Ordinal))
            return new Diagnostic(CustomCodeCodes.Shape,
                $"this definition expects custom code in '{language ?? "nothing"}', and the supplied "
                + $"sources are '{bundle.Language}'.");

        var hash = AppModel.Str(declared["hash"]);
        if (!string.Equals(hash, bundle.Hash, StringComparison.Ordinal))
            return new Diagnostic(CustomCodeCodes.Shape,
                "the custom sources do not match the definition that was compiled from them — the "
                + "code changed after it was checked. Building would produce an application whose "
                + "own hash vouches for code that is not in it. Run the build again.");

        return null;
    }

    /// <summary>One file per source, byte-for-byte under a banner.</summary>
    public static IEnumerable<GeneratedFile> Emit(CustomSourceBundle? bundle)
    {
        if (bundle is null) yield break;

        foreach (var (path, content) in bundle.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            yield return new GeneratedFile($"{Directory}/{path}", Banner(path) + content);
    }

    /// <summary>
    /// One adapter per declared hook: the runtime interface on the outside, the author's own method
    /// on the inside.
    ///
    /// <para><b>Why an adapter rather than making the author implement the interface.</b> Six
    /// generic interfaces, each with its own method name and its own parameter list, is a lot to
    /// know before writing a line — and a class that wanted two hooks would have to implement two of
    /// them and remember which method belonged to which. An attribute on a method says the same
    /// thing in one line, and this is the small amount of code that makes the two equivalent.</para>
    ///
    /// <para>Emitted into <c>api/Hooks/</c>, beside the generated hooks that already live there, and
    /// deliberately NOT into <c>api/Custom/</c> — that directory mirrors the author's own tree, and a
    /// generated file dropped into it could collide with one they wrote.</para>
    /// </summary>
    public static IEnumerable<GeneratedFile> Adapters(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var hook in app.CustomHooks)
        {
            if (app.Entities.FirstOrDefault(e => e.Key == hook.Entity) is not { } entity) continue;

            var source = new Source();
            source.Line("using Cordango.Standalone.Hooks;");
            source.Line($"using {app.Namespace}.Entities;");
            source.Line();
            source.Line($"namespace {app.Namespace}.Hooks;");
            source.Line();
            source.Line($"/// <summary>Runs {hook.Type}.{hook.Method} on {hook.Event.Replace('_', ' ')} of a");
            source.Line($"/// {entity.Label}. Written from the [{Attribute(hook)}] on that method.</summary>");
            source.Open($"public sealed class {hook.AdapterName}("
                + $"global::{app.Namespace}.Custom.{hook.Type} custom) "
                + $": {hook.Interface}<{entity.TypeName}>");

            var parameters = hook.Paired
                ? $"{entity.TypeName} record, {entity.TypeName} before, RecordContext context, CancellationToken ct"
                : $"{entity.TypeName} record, RecordContext context, CancellationToken ct";

            var arguments = hook.Paired ? "record, before, context, ct" : "record, context, ct";

            source.Line($"public Task {hook.InterfaceMethod}({parameters}) =>");
            source.Indent().Line($"custom.{hook.Method}({arguments});").Outdent();
            source.Close();

            yield return new GeneratedFile($"api/Hooks/{hook.AdapterName}.cs", source.ToString());
        }
    }

    private static string Attribute(CustomHookModel hook) =>
        string.Concat(hook.Event.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>
    /// What this file is and where the real one lives.
    ///
    /// <para>Carries no version. A patch release of the CLI that changes nothing else must still
    /// produce byte-identical output, and a banner naming the version that wrote it would make every
    /// generated application differ on every upgrade for no reason anybody could act on.</para>
    /// </summary>
    private static string Banner(string path) =>
        $"// Generated from custom/dotnet/{path} by cordango build. Edit THAT file:\n"
        + "// this copy is replaced every time the application is generated.\n\n";
}
