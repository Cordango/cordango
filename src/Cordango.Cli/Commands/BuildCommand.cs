// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cli.Generate;
using Cordango.Cli.Workspace;
using Cordango.SourceGen;

namespace Cordango.Cli.Commands;

/// <summary>
/// Deterministic artifacts under <c>.cordango/build/&lt;key&gt;/</c>.
///
/// <para><b>Publish only after the whole pipeline passes.</b> Artifacts are written to a temporary
/// directory and moved into place, so a failed build never leaves a half-written definition that the
/// next command reads as this app's current shape.</para>
///
/// <para><b>Nothing here is source.</b> <c>.cordango/</c> is generated and gitignored, and the scaffold's
/// agent instructions say so explicitly — an agent editing <c>app.definition.json</c> instead of the
/// semantic files would produce changes that vanish on the next build.</para>
/// </summary>
public static class BuildCommand
{
    public const string BuildDirectory = ".cordango";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(Args args, Output output)
    {
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        // `--target` turns build into GENERATION: the same pipeline, then a whole application
        // written to a directory of your own. Without it, build produces the compiled artifacts
        // under .cordango/ and nothing else.
        IAppSourceGenerator? target = null;
        if (args.Value("target") is { Length: > 0 } requested)
        {
            target = Targets.Find(requested);
            if (target is null)
                return output.Fail($"no target called '{requested}'", [$"known targets: {Targets.Known}"],
                    new JsonObject { ["target"] = requested });
        }

        if (target is not null && args.Value("out") is not { Length: > 0 })
            return output.Fail("--target needs --out",
                ["a generated application is written to a directory you name, for example: "
                 + "cordango build --target standalone --out ./expenses-app"],
                new JsonObject { ["target"] = target.Id });

        var reports = selection.Apps.Select(Pipeline.Check).ToList();
        var incoherent = reports.Where(r => !r.Coherent).ToList();

        if (incoherent.Count > 0)
        {
            return output.Fail("nothing was built — the source does not hold together",
                incoherent.SelectMany(r => r.Errors.Select(e => $"{r.AppKey}: {e}")),
                new JsonObject { ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]) });
        }

        var written = new List<string>();
        foreach (var report in reports)
        {
            var directory = Path.Combine(selection.Workspace.Root, BuildDirectory, "build", report.AppKey);

            AtomicFile.Write(Path.Combine(directory, "app.definition.json"),
                report.Definition!.ToJsonString(Pretty) + "\n");
            AtomicFile.Write(Path.Combine(directory, "manifest.json"),
                report.Manifest!.ToJsonString(Pretty) + "\n");

            // Diagnostics carry what must NOT influence the hashed artifacts: the real build time,
            // the tool version, and whether the app is finished. Separating them is what lets the
            // definition and manifest be byte-identical across two runs of the same source.
            AtomicFile.Write(Path.Combine(directory, "diagnostics.json"), new JsonObject
            {
                ["app"] = report.AppKey,
                ["definitionHash"] = report.DefinitionHashHex,
                ["valid"] = report.Valid,
                ["cordangoVersion"] = VersionCommand.CliVersion,
                ["builtAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["incomplete"] = new JsonArray([.. report.Errors.Select(e => (JsonNode)e!)]),
                ["fills"] = new JsonArray([.. report.Fills.Select(f => (JsonNode)f!)]),
            }.ToJsonString(Pretty) + "\n");

            written.Add($"{BuildDirectory}/build/{report.AppKey}");
        }

        if (target is not null)
            return Generate(args, output, reports, target);

        return output.Ok(
            new JsonObject
            {
                ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]),
                ["written"] = new JsonArray([.. written.Select(p => (JsonNode)p!)]),
            },
            w =>
            {
                foreach (var report in reports)
                    w.WriteLine($"{report.AppKey,-24} {report.DefinitionHashHex?[..12]}  "
                        + $"→ {BuildDirectory}/build/{report.AppKey}");
            });
    }

    /// <summary>
    /// Hand one application to a generator and write what it produces.
    ///
    /// <para>One app at a time, on purpose: a generated application is a repository, and two of them
    /// in one directory is not a thing. Selecting several and asking for a target is a mistake worth
    /// naming rather than resolving.</para>
    /// </summary>
    private static int Generate(Args args, Output output, List<CheckReport> reports, IAppSourceGenerator target)
    {
        if (reports.Count != 1)
            return output.Fail($"--target builds one application, and {reports.Count} were selected",
                ["name one with --app, for example: cordango build --app "
                 + reports[0].AppKey + " --target " + target.Id + " --out ./app"],
                new JsonObject { ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.AppKey!)]) });

        var report = reports[0];
        var root = Path.GetFullPath(args.Value("out")!);

        // The ONE construction site of a CompiledAppArtifact: definition and manifest come out of
        // the same pipeline run, so they cannot describe two different documents.
        // AsObject rather than a cast: the pipeline types its definition as a node, and a
        // definition that is not an object would have failed the gate long before here.
        var artifact = new CompiledAppArtifact(
            report.Definition!.AsObject(),
            report.Manifest!,
            report.DefinitionHashHex!,
            new CompilerInfo(
                report.Manifest!["build"]?["compiler"]?.GetValue<string>() ?? "unknown",
                report.Manifest!["build"]?["manifestVersion"]?.GetValue<string>() ?? "1"));

        var options = new JsonObject
        {
            // `--allow-partial-ui` was the original spelling, from when the only expected gap was a
            // screen the emitters could not draw. Behaviour can be missing too, so the flag has a
            // name that covers both — and the old one keeps working, because it is written down in
            // build scripts.
            ["allowIncomplete"] = args.Has("allow-incomplete") || args.Has("allow-partial-ui"),
            ["seed"] = int.TryParse(args.Value("seed"), out var seed) ? seed : 42,

            // `--runtime package` references Cordango.Standalone from a feed instead of checking its
            // source in. The cleaner shape, and it needs the package to be restorable — which it is
            // not until it is published, so source is the default.
            ["runtimeAsPackage"] = string.Equals(args.Value("runtime"), "package", StringComparison.OrdinalIgnoreCase),
        };

        var result = target.Generate(new GenerateRequest(artifact, options));

        if (!result.Ok)
            return output.Fail($"{target.Id} cannot build {report.AppKey}",
                result.Errors.Select(Describe),
                new JsonObject
                {
                    ["app"] = report.AppKey,
                    ["target"] = target.Id,
                    ["errors"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)Diagnostics(e))]),
                });

        var draft = new BuildMetadataDraft(
            artifact.DefinitionHash, artifact.Compiler, target.Id, target.Version, result.Warnings);

        var write = GeneratedFileWriter.Write(root, result, draft, dryRun: args.Has("dry-run"));

        if (!write.Ok)
            return output.Fail("nothing was written", write.Errors.Select(Describe),
                new JsonObject { ["errors"] = new JsonArray([.. write.Errors.Select(e => (JsonNode)Diagnostics(e))]) });

        return output.Ok(
            new JsonObject
            {
                ["app"] = report.AppKey,
                ["target"] = target.Id,
                ["out"] = root,
                ["files"] = write.Written.Count,
                ["deleted"] = write.Deleted.Count,
                ["partial"] = result.Warnings.Count > 0,
                ["warnings"] = new JsonArray([.. result.Warnings.Select(w => (JsonNode)Diagnostics(w))]),
            },
            w =>
            {
                w.WriteLine($"{report.AppKey} -> {root}");
                w.WriteLine($"{write.Written.Count} files written"
                    + (write.Deleted.Count > 0 ? $", {write.Deleted.Count} removed" : ""));

                foreach (var warning in result.Warnings.Take(10)) w.WriteLine("  ! " + Describe(warning));
                if (result.Warnings.Count > 10) w.WriteLine($"  ! and {result.Warnings.Count - 10} more");

                w.WriteLine();
                w.WriteLine("next:");
                w.WriteLine($"  cd {root}");
                w.WriteLine("  docker compose up --build");
                w.WriteLine();
                w.WriteLine("then open http://localhost:8080 — the first screen asks you to create");
                w.WriteLine("the administrator account. There is no password to go and look up.");
            });
    }

    private static string Describe(Diagnostic diagnostic) =>
        diagnostic.JsonPath is { Length: > 0 } path
            ? $"{diagnostic.Code} at {path}: {diagnostic.Message}"
            : $"{diagnostic.Code}: {diagnostic.Message}";

    private static JsonObject Diagnostics(Diagnostic diagnostic) => new()
    {
        ["code"] = diagnostic.Code,
        ["message"] = diagnostic.Message,
        ["path"] = diagnostic.JsonPath,
    };
}
