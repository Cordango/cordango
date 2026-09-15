// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cli.Generate;
using Cordango.Compile;
using Cordango.Cli.Workspace;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;

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
        if (ConfigureCommand.RetiredOut(args, output) is { } retired) return retired;

        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        var config = Configuration(args, selection.Workspace, output, out var configExit);
        if (configExit is not null) return configExit.Value;

        // What is being built. `--target` on the command line wins over the file, because somebody
        // who typed it is asking for this one build to differ; otherwise the workspace's own answer,
        // which is the whole reason `cordango build` can be run with no flags at all.
        var requested = args.Value("target") is { Length: > 0 } named ? named : config?.Target;

        // The platform is a target with no generator behind it — nothing is emitted, the definition
        // is what travels. It is checked FIRST because the refusal it can produce is about the
        // connection rather than about the app, and finding that out after a full compile is a worse
        // way to learn it.
        var platform = requested is { Length: > 0 } && string.Equals(requested, BuildConfig.Platform,
            StringComparison.OrdinalIgnoreCase);

        if (platform && !ConfigureCommand.RequireConnection(args, selection.Workspace, output, out var offline))
            return offline;

        IAppSourceGenerator? target = null;
        if (!platform && requested is { Length: > 0 })
        {
            target = Targets.Find(requested);
            if (target is null)
                return output.Fail($"no target called '{requested}'", [$"known targets: {Targets.Known}, "
                    + BuildConfig.Platform],
                    new JsonObject { ["target"] = requested });
        }

        var reports = selection.Apps.Select(a => Pipeline.Check(a, selection.Roster)).ToList();
        var incoherent = reports.Where(r => !r.Coherent).ToList();

        if (incoherent.Count > 0)
        {
            return output.Fail("nothing was built — the source does not hold together",
                incoherent.SelectMany(r => r.Errors.Select(e => $"{r.AppKey}: {e}")),
                new JsonObject { ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]) });
        }

        // The platform interprets a definition; it does not compile one. An application carrying
        // custom code has nothing there to run it, so it is refused HERE rather than accepted and
        // left with blank columns nobody can explain.
        var unrunnable = reports
            .Where(r => r.Definition is not null)
            .SelectMany(r => PlatformCapabilities.Validate(r.Definition).Select(d => $"{r.AppKey}: {d}"))
            .ToList();

        if (platform && unrunnable.Count > 0)
            return output.Fail("nothing was built — this workspace cannot run on the platform", unrunnable);

        // The record types an editor reads while somebody writes custom code beside them. Only for an
        // app that HAS a custom directory — every other workspace is untouched — and never under
        // --dry-run, which promises to write nothing.
        if (!args.Has("dry-run"))
        {
            foreach (var loaded in selection.Apps)
            {
                var custom = Path.Combine(loaded.Directory, CustomSourceLoader.DirectoryName, "dotnet");
                if (!System.IO.Directory.Exists(custom)) continue;

                CustomCommand.Sync(loaded, Naming.Pascal(loaded.App?.Key ?? loaded.Key), custom);
            }
        }

        var written = new List<string>();
        foreach (var report in reports)
        {
            var directory = Path.Combine(selection.Workspace.Root, BuildDirectory, "build", report.AppKey);

            AtomicFile.Write(Path.Combine(directory, "app.definition.json"),
                report.Definition!.ToJsonString(Pretty) + "\n");
            AtomicFile.Write(Path.Combine(directory, "manifest.json"),
                report.Manifest!.ToJsonString(Pretty) + "\n");

            // The contract is written by ContractWriter and never by this method's own formatting:
            // the platform writes the same bytes for the same definition, and a trailing newline
            // added here rather than there is exactly how that stops being true.
            if (report.Contract is { } contract)
                AtomicFile.Write(Path.Combine(directory, "contract.json"), ContractWriter.Text(contract));

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
            return Generate(args, output, selection.Workspace, reports, target, config);

        return output.Ok(
            new JsonObject
            {
                ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]),
                ["written"] = new JsonArray([.. written.Select(p => (JsonNode)p!)]),
                ["target"] = platform ? BuildConfig.Platform : null,

                // The note explaining this goes to stderr, which `--json` silences. A caller that
                // only ever sees the payload still has to be able to tell "this workspace has not
                // been asked where its apps run" from "it was asked, and the answer is artifacts".
                ["configured"] = config is not null,
            },
            w =>
            {
                foreach (var report in reports)
                    w.WriteLine($"{report.AppKey,-24} {report.DefinitionHashHex?[..12]}  "
                        + $"→ {BuildDirectory}/build/{report.AppKey}");

                // The platform target's build produces the definition and stops. Sending it is a
                // separate, deliberate act — `build` has never deployed anything and starting now
                // would make an ordinary command outward-facing.
                if (!platform) return;
                w.WriteLine();
                w.WriteLine("next:");
                w.WriteLine("  cordango publish" + Ansi.Dim("     # send it to the instance and make it live"));
            });
    }

    /// <summary>
    /// This workspace's build configuration, asking for one if nobody has written it yet.
    ///
    /// <para><b>Asking is best-effort; refusing is not on the table.</b> A workspace with no
    /// configuration still builds its definition artifacts exactly as it did before this block
    /// existed — that is what <c>cordango build</c> has always meant, it needs no decisions, and
    /// failing a command that used to work because a file is missing would be a bad trade for a
    /// convenience. When there IS somebody at a terminal, the two questions get asked and the answer
    /// is committed; when there is not, the assumption is printed instead of being made
    /// silently.</para>
    /// </summary>
    /// <param name="exit">Set only when the interview itself failed — a platform target with no
    /// connection, say. Null means carry on, configured or not.</param>
    private static BuildConfig? Configuration(Args args, WorkspaceFile workspace, Output output,
        out int? exit)
    {
        exit = null;

        if (workspace.Build is { } existing)
        {
            if (existing.Problems() is { Count: > 0 } problems)
            {
                exit = output.Fail($"the build configuration in {WorkspaceFile.FileName} is not usable",
                    problems, code: ExitCodes.Usage);
            }

            return existing;
        }

        // An explicit --target is the caller answering the question for this one run. Interrupting
        // them with the question they just answered would be rude and, in a script, wrong.
        if (args.Value("target") is { Length: > 0 }) return null;

        if (Interview.Open(args) is not { } interview)
        {
            output.Note($"no build configuration in {WorkspaceFile.FileName} — building the "
                + "definition artifacts only. `cordango configure` decides where this workspace's "
                + "apps are meant to run, and then `cordango build` does the whole job.");
            return null;
        }

        var chosen = ConfigureCommand.Ask(interview, existing: null);
        var written = ConfigureCommand.Write(args, workspace, chosen, output);

        if (written != ExitCodes.Ok)
        {
            exit = written;
            return null;
        }

        return chosen;
    }

    /// <summary>
    /// Hand the selected applications to a generator, as ONE workspace, and write what it produces.
    ///
    /// <para><b>One directory per workspace, chosen rather than configured.</b> The unit of a build
    /// is the workspace: the apps in it share a database, a sign-in, a directory and a shell, so they
    /// are one repository at <c>generated/&lt;workspace&gt;/</c> rather than several that would each
    /// need their own container and could never see each other. A workspace holding one app is a
    /// workspace of one, built by the same code path — a path that only runs when the count is one is
    /// a path that drifts.</para>
    /// </summary>
    private static int Generate(Args args, Output output, WorkspaceFile workspace,
        List<CheckReport> reports, IAppSourceGenerator target, BuildConfig? config)
    {
        // Anchored to the workspace root, never to the working directory: `cordango build` run from
        // inside apps/expenses/entities/ has to write to the same place it does from the top, or a
        // second run from a second directory generates a second copy.
        var root = Path.GetFullPath(Path.Combine(workspace.Root, BuildConfig.OutFor(workspace)));

        var artifacts = new List<CompiledAppArtifact>(reports.Count);
        var custom = new Dictionary<string, CustomSourceBundle>(StringComparer.Ordinal);

        foreach (var report in reports)
        {
            // The ONE construction site of a CompiledAppArtifact: definition and manifest come out of
            // the same pipeline run, so they cannot describe two different documents.
            // AsObject rather than a cast: the pipeline types its definition as a node, and a
            // definition that is not an object would have failed the gate long before here.
            artifacts.Add(new CompiledAppArtifact(
                report.Definition!.AsObject(),
                report.Manifest!,
                report.DefinitionHashHex!,
                new CompilerInfo(
                    report.Manifest!["build"]?["compiler"]?.GetValue<string>() ?? "unknown",
                    report.Manifest!["build"]?["manifestVersion"]?.GetValue<string>() ?? "1")));

            // The SAME bundle the check hashed, never a second read. The generator verifies it
            // against the hash now in the definition, and a save between the two would otherwise
            // turn into a refusal nobody could explain.
            if (report.CustomSources is { } bundle) custom[report.AppKey] = bundle;
        }

        var identity = new WorkspaceIdentity(Naming.Sanitise(workspace.Name), workspace.Name);
        var compiled = new CompiledWorkspaceArtifact(identity, artifacts);

        var result = target.Generate(new GenerateRequest(
            compiled, Options(args, config), custom.Count > 0 ? custom : null));

        if (!result.Ok)
            return output.Fail($"{target.Id} cannot build {identity.Key}",
                result.Errors.Select(Describe),
                new JsonObject
                {
                    ["workspace"] = identity.Key,
                    ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.AppKey)]),
                    ["target"] = target.Id,
                    ["errors"] = new JsonArray([.. result.Errors.Select(e => (JsonNode)Diagnostics(e))]),
                });

        var draft = new BuildMetadataDraft(
            WorkspaceHash(artifacts), artifacts[0].Compiler, target.Id, target.Version, result.Warnings);

        var write = GeneratedFileWriter.Write(root, result, draft, dryRun: args.Has("dry-run"));

        if (!write.Ok)
            return output.Fail("nothing was written", write.Errors.Select(Describe),
                new JsonObject { ["errors"] = new JsonArray([.. write.Errors.Select(e => (JsonNode)Diagnostics(e))]) });

        var payload = new JsonObject
        {
            ["target"] = target.Id,
            ["workspace"] = identity.Key,
            ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.AppKey)]),
            ["out"] = root,
            ["files"] = write.Written.Count,
            ["deleted"] = write.Deleted.Count,
            ["partial"] = result.Warnings.Count > 0,
            ["warnings"] = new JsonArray([.. result.Warnings.Select(w => (JsonNode)Diagnostics(w))]),
        };

        // `app` stays for a workspace of one. `--json` callers written when a build WAS one app are
        // not asked to move for a workspace that still holds one.
        if (reports.Count == 1) payload["app"] = reports[0].AppKey;

        return output.Ok(payload, w =>
        {
            w.WriteLine($"{identity.Key} -> {root}");
            w.WriteLine(reports.Count == 1
                ? $"  {reports[0].AppKey}"
                : $"  {reports.Count} applications: {string.Join(", ", reports.Select(r => r.AppKey))}");
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

    /// <summary>
    /// One hash for the whole workspace, recorded in <c>cordango.build.json</c>.
    ///
    /// <para>A workspace of one is its app's own definition hash, unchanged — the metadata of an
    /// application that was always alone should not move because the concept around it grew a name.
    /// Several apps fold into a hash over their <c>key:hash</c> lines in build order, so it moves
    /// when any app changes AND when their ORDER changes. The order is part of the build: it fixes
    /// the <c>ModelBuilder</c> calls and therefore the migration.</para>
    /// </summary>
    private static string WorkspaceHash(IReadOnlyList<CompiledAppArtifact> apps)
    {
        if (apps.Count == 1) return apps[0].DefinitionHash;

        var lines = string.Join(
            "\n", apps.Select(a => $"{a.Manifest["key"]?.GetValue<string>() ?? "?"}:{a.DefinitionHash}"));
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lines)));
    }

    /// <summary>
    /// What the generator is told, from the flags and then from the file.
    ///
    /// <para>A flag beats the configuration for this run only, which is the point of a flag. The
    /// configuration beats the built-in default, which is the point of the configuration.</para>
    /// </summary>
    private static JsonObject Options(Args args, BuildConfig? config) => new()
    {
        // `--allow-partial-ui` was the original spelling, from when the only expected gap was a
        // screen the emitters could not draw. Behaviour can be missing too, so the flag has a
        // name that covers both — and the old one keeps working, because it is written down in
        // build scripts.
        ["allowIncomplete"] = args.Has("allow-incomplete") || args.Has("allow-partial-ui")
            || (config?.AllowIncomplete ?? false),

        ["seed"] = int.TryParse(args.Value("seed"), out var seed)
            ? seed
            : config?.Seed ?? BuildConfig.DefaultSeed,

        // Cordango.Standalone is restored from a feed like any other dependency, so a generated
        // repository holds the user's application and nothing else. `--runtime source` checks
        // its source in as a sibling project instead — for working on the runtime, or for
        // building against a version no feed has yet.
        ["runtimeAsPackage"] = !string.Equals(
            args.Value("runtime") ?? config?.Runtime ?? BuildConfig.RuntimePackage,
            BuildConfig.RuntimeSource, StringComparison.OrdinalIgnoreCase),
    };

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
