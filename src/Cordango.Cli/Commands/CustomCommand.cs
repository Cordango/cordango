// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Generate;
using Cordango.Cli.Workspace;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;

namespace Cordango.Cli.Commands;

/// <summary>
/// Setting up, and keeping up to date, the place an application's own code lives.
///
/// <para><b>Why there is a command at all.</b> The code itself is ordinary C# in an ordinary
/// directory, and nothing here is required to write it. What is required is a project file, so that
/// an editor can resolve <c>Invoice</c> and <c>RecordContext</c> while somebody is typing — and
/// getting that project file right by hand means knowing which package, which version and which
/// namespaces, none of which is interesting.</para>
///
/// <para><b>The project is for the EDITOR and is never built into anything.</b> The generated
/// application compiles its own copy of these sources; this one exists so the red squiggles go away
/// before the build, and it deliberately references no generated project — that would define every
/// entity twice.</para>
/// </summary>
public static class CustomCommand
{
    /// <summary>The language directory. One today; the name is what selects the scanner.</summary>
    private const string Language = "dotnet";

    /// <summary>Entity types, copied in so the editor can see them. Gitignored, rewritten on every
    /// build, and never read by the generated application.</summary>
    public const string GeneratedDirectory = ".generated";

    public static int Run(Args args, Output output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (Selection.Resolve(args, output, out var failure) is not { } selection) return failure;
        if (selection.Apps.Count != 1)
        {
            return output.Fail("say which app the code belongs to",
                ["this workspace holds more than one app — pass --app <name>"],
                code: ExitCodes.Usage);
        }

        var app = selection.Apps[0];
        var directory = Path.Combine(app.Directory, CustomSourceLoader.DirectoryName, Language);
        var written = new List<string>();

        System.IO.Directory.CreateDirectory(directory);

        var key = app.App?.Key ?? app.Key;
        var appNamespace = Naming.Pascal(key);

        Write(Path.Combine(directory, $"{appNamespace}.Custom.csproj"), Project(), written, app.Directory);
        Write(Path.Combine(directory, ".gitignore"), GitIgnore, written, app.Directory);

        var sample = Path.Combine(directory, "Rules.cs");
        if (!File.Exists(sample)) Write(sample, Sample(appNamespace), written, app.Directory);

        var entities = Sync(app, appNamespace, directory);

        return output.Ok(new JsonObject
        {
            ["app"] = key,
            ["directory"] = Path.GetRelativePath(selection.Workspace.Root, directory).Replace('\\', '/'),
            ["written"] = new JsonArray([.. written.Select(w => (JsonNode)w)]),
            ["entities"] = entities,
        }, w =>
        {
            foreach (var file in written) w.WriteLine("  " + file);

            w.WriteLine();
            w.WriteLine(entities == 0
                ? "Run `cordango build` and reopen the folder: the entity types an editor needs are"
                : $"{entities} entity types are in {GeneratedDirectory}/, so an editor can resolve them.");

            if (entities == 0) w.WriteLine("written there by the build.");
        });
    }

    /// <summary>
    /// Refresh the entity copies an editor reads.
    ///
    /// <para><b>Reconciled, not merely written.</b> An entity that has left the definition has to
    /// lose its file, or the editor goes on offering a type the application no longer has and the
    /// first anybody hears of it is a compile error in the generated app.</para>
    ///
    /// <para>Returns the number of entities, or -1 when there was nothing to sync from — an app that
    /// has never compiled has no manifest and no entity types to copy.</para>
    /// </summary>
    public static int Sync(LoadedApp app, string appNamespace, string directory)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(directory);

        var report = Pipeline.Check(app, null);
        if (report.Manifest is null) return 0;

        var model = AppModel.From(new CompiledAppArtifact(
            report.Definition?.AsObject() ?? [], report.Manifest,
            report.DefinitionHashHex ?? "unhashed", new CompilerInfo("cordango", "1")));

        var into = Path.Combine(directory, GeneratedDirectory, "Entities");
        System.IO.Directory.CreateDirectory(into);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in model.Entities)
        {
            var file = Cordango.SourceGen.DotNet.Emit.EntityEmitter.Emit(model, entity);
            var name = Path.GetFileName(file.RelativePath);
            keep.Add(name);
            AtomicFile.Write(Path.Combine(into, name), file.Content);
        }

        foreach (var stale in System.IO.Directory.EnumerateFiles(into, "*.cs"))
            if (!keep.Contains(Path.GetFileName(stale)))
                File.Delete(stale);

        AtomicFile.Write(Path.Combine(into, "README.md"), Readme);

        return model.Entities.Count;
    }

    private static void Write(string path, string content, List<string> written, string relativeTo)
    {
        AtomicFile.Write(path, content);
        written.Add(Path.GetRelativePath(relativeTo, path).Replace('\\', '/'));
    }

    /// <summary>
    /// The editor project.
    ///
    /// <para>No <c>ProjectReference</c>, deliberately: the generated application contains a copy of
    /// these same files, so referencing it would define every type twice and nothing would resolve.
    /// The entity types arrive as source under <c>.generated/</c> instead, which the default glob
    /// picks up.</para>
    ///
    /// <para>The runtime version is pinned to the one the generator emits, so an editor never
    /// type-checks against different attributes from the ones the build will use.</para>
    /// </summary>
    private static string Project() =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <!--
            FOR YOUR EDITOR. Nothing builds this project: `cordango build` copies the sources beside
            it into the generated application and that is what compiles. It exists so an editor can
            resolve Invoice, RecordContext and the Cordango attributes while you are typing.

            .generated/Entities holds copies of the application's record types, written by
            `cordango build` and gitignored. On a fresh clone they are not there yet and the editor
            will show errors until you build once.
          -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Cordango.Standalone" Version="{Cordango.SourceGen.DotNet.Scaffold.RuntimeVersion}" />
          </ItemGroup>

          <ItemGroup>
            <Using Include="Cordango.Standalone.Custom" />
            <Using Include="Cordango.Standalone.Hooks" />
            <Using Include="Cordango.Standalone.Records" />
          </ItemGroup>

        </Project>

        """;

    private const string GitIgnore =
        """
        # Written by `cordango build` so an editor can resolve the record types.
        .generated/

        bin/
        obj/

        """;

    private const string Readme =
        """
        # Generated

        Copies of this application's record types, written by `cordango build` so that an editor can
        resolve them while you write custom code beside them.

        Nothing here is read by the generated application, and nothing here is yours to edit: every
        file is replaced on the next build, and one whose entity has left the definition is deleted.

        """;

    private static string Sample(string appNamespace) =>
        $$"""
        namespace {{appNamespace}}.Custom;

        /// <summary>
        /// Code this application carries, called by name from the definition beside it.
        ///
        /// <para>A FUNCTION is called from a computed field as `custom.<name>(...)`. It must be pure
        /// and immediate: a figure is worked out when the row is written and stored beside it, so one
        /// that reads a clock or the outside world is right once and wrong afterwards. Parameters and
        /// returns are `decimal?`, `bool?` or `string?` — every one nullable, because a computed
        /// value can be unknown.</para>
        ///
        /// <para>A HOOK runs around a write and has none of those restrictions: it may be async and
        /// may do I/O. Throw RecordException to refuse the write with a message the caller sees.</para>
        /// </summary>
        [CordangoFunctions]
        public static class Rules
        {
            [CordangoFunction("round", Description = "The nearest whole number, halves away from zero.")]
            public static decimal? Round(decimal? value) =>
                value is null ? null : decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero);
        }

        """;
}
