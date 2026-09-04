// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Generate;
using Cordango.Cli.Workspace;
using Cordango.SourceGen;

namespace Cordango.Cli.Commands;

/// <summary>
/// Parse, lower, validate. The command an agent runs after every change and CI runs on every commit.
///
/// <para><b>Coherent and valid are reported separately, and only one of them fails the command.</b>
/// An app being unfinished — no screens yet, nothing to create — is a normal state halfway through
/// authoring, and a check that failed on it would train an agent to ignore the exit code. Incoherent
/// is a real failure: the document does not hold together, and whatever is built on it next is built
/// on sand.</para>
///
/// <para><b><c>--target</c> asks a second, different question.</b> Without it: is this a valid
/// Cordango application? With it: can THAT target also build it? The two are not the same and
/// neither implies the other — every target supports less than the language does, deliberately, and
/// an application using a platform-only feature is correct rather than broken. So a capability
/// refusal reports its own CORD21xx code and never reads as "your app is wrong".</para>
/// </summary>
public static class CheckCommand
{
    public static int Run(Args args, Output output)
    {
        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        IAppSourceGenerator? target = null;
        var requested = args.Value("target");

        // The platform withholds nothing: it runs the whole language, and every generator supports
        // less than it does. So `--target platform` asks a question whose answer is always the
        // ordinary one, and saying so is better than "no target called platform" — which would read
        // as though the word were wrong.
        var platform = requested is { Length: > 0 }
            && string.Equals(requested, BuildConfig.Platform, StringComparison.OrdinalIgnoreCase);

        if (!platform && requested is { Length: > 0 })
        {
            target = Targets.Find(requested);
            if (target is null)
                return output.Fail($"no target called '{requested}'",
                    [$"known targets: {Targets.Known}, {BuildConfig.Platform}"],
                    code: ExitCodes.Usage);
        }

        var reports = selection.Apps.Select(Pipeline.Check).ToList();

        // Capability checking needs a definition, which an incoherent app does not have. It is not
        // skipped quietly: an app that fails below never reaches the "compatible" line either.
        var unsupported = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        if (target is not null)
            foreach (var report in reports)
                if (report.Definition is JsonObject definition)
                    unsupported[report.AppKey] = TargetValidator.Validate(definition, target.Capabilities);

        var incoherent = reports.Where(r => !r.Coherent).ToList();
        var payload = new JsonObject
        {
            ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]),
        };

        if (platform) payload["target"] = BuildConfig.Platform;

        if (target is not null)
        {
            payload["target"] = target.Id;
            payload["unsupported"] = new JsonArray([.. unsupported
                .SelectMany(kv => kv.Value.Select(d => (JsonNode)new JsonObject
                {
                    ["app"] = kv.Key,
                    ["code"] = d.Code,
                    ["message"] = d.Message,
                    ["path"] = d.JsonPath,
                }))]);
        }

        if (incoherent.Count > 0)
        {
            return output.Fail(
                incoherent.Count == 1
                    ? $"{incoherent[0].AppKey} does not hold together"
                    : $"{incoherent.Count} apps do not hold together",
                incoherent.SelectMany(r => r.Errors.Select(e => $"{r.AppKey}: {e}")),
                payload);
        }

        var blocked = unsupported.Where(kv => kv.Value.Count > 0).ToList();
        if (blocked.Count > 0)
        {
            return output.Fail(
                blocked.Count == 1
                    ? $"{blocked[0].Key} cannot be built by {target!.Id}"
                    : $"{blocked.Count} apps cannot be built by {target!.Id}",
                blocked.SelectMany(kv => kv.Value.Select(d => $"{kv.Key}: {d}")),
                payload);
        }

        return output.Ok(payload, w =>
        {
            foreach (var report in reports)
            {
                // "incomplete" rather than "invalid": the app is not wrong, it is not finished, and
                // the difference is the whole reason CandidateOutcome carries two booleans.
                var state = report.Valid ? "ok" : "ok, incomplete";
                if (target is not null) state += $", builds with {target.Id}";
                else if (platform) state += ", runs on the platform";
                w.WriteLine($"{report.AppKey,-24} {state}");

                if (!report.Valid)
                    foreach (var error in report.Errors) w.WriteLine($"    still needed: {error}");

                foreach (var fill in report.Fills) w.WriteLine($"    filled: {fill}");

                // Notes are neither errors nor fills: legal, compiling, and still worth saying. An
                // implicit dependency prints the `uses` entry that would settle it, because the
                // author's next move is to paste it and the alternative is guessing the syntax.
                foreach (var note in report.Notes)
                {
                    w.WriteLine($"    {note.Severity}: {note.Message}");
                    if (note.Suggestion is { Length: > 0 } fix) w.WriteLine($"        declare it: {fix}");
                }
            }

            if (reports.Count == 0) w.WriteLine("no apps registered in " + WorkspaceFile.FileName);
        });
    }
}
