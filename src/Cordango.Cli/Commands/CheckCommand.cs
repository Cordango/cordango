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

        var reports = selection.Apps.Select(a => Pipeline.Check(a, selection.Roster)).ToList();

        // Capability checking needs a definition, which an incoherent app does not have. It is not
        // skipped quietly: an app that fails below never reaches the "compatible" line either.
        //
        // The roster is what THIS CHECK is about, which is why it comes from the narrowed selection
        // and not from the whole workspace. `check --target standalone` asks "can this workspace be
        // built?", and a sibling reference is fine because the sibling will be in the deployment.
        // `check --target standalone --app purchase_requests` asks the narrower and more useful
        // question — "can this one ship on its own?" — and there the same reference is a refusal.
        // Reading the whole workspace either way would answer the first question twice and leave
        // the second unaskable.
        var siblings = reports.Select(r => r.AppKey).ToHashSet(StringComparer.Ordinal);

        var unsupported = new Dictionary<string, IReadOnlyList<Diagnostic>>(StringComparer.Ordinal);
        if (target is not null)
            foreach (var report in reports)
                if (report.Definition is JsonObject definition)
                    unsupported[report.AppKey] =
                        TargetValidator.Validate(definition, target.Capabilities, siblings);

        // The platform used to withhold nothing, and almost still doesn't — see the note above. The
        // exception is code: it interprets a definition rather than compiling one, so a method
        // somebody wrote has nothing to run it. Asked here so that "will this run there?" is
        // answered before somebody publishes and finds a column silently blank.
        if (platform)
            foreach (var report in reports)
                if (report.Definition is { } definition
                    && PlatformCapabilities.Validate(definition) is { Count: > 0 } refusals)
                {
                    unsupported[report.AppKey] = refusals;
                }

        var incoherent = reports.Where(r => !r.Coherent).ToList();
        var payload = new JsonObject
        {
            ["apps"] = new JsonArray([.. reports.Select(r => (JsonNode)r.ToJson())]),
        };

        if (platform) payload["target"] = BuildConfig.Platform;

        if (target is not null || platform)
        {
            if (target is not null) payload["target"] = target.Id;
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
            // "run on" for the platform, "built by" for a generator. The platform does not build
            // anything, and telling somebody their app cannot be BUILT by it would send them
            // looking for a build step that does not exist.
            var what = target?.Id ?? BuildConfig.Platform;
            var verb = target is null ? "run on" : "be built by";

            return output.Fail(
                blocked.Count == 1
                    ? $"{blocked[0].Key} cannot {verb} {what}"
                    : $"{blocked.Count} apps cannot {verb} {what}",
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
