// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;
using Cordango.Cord;

namespace Cordango.Cli.Commands;

/// <summary>
/// Apply semantic operations to one app and rewrite the affected source files.
///
/// <para><b>This is the same engine the hosted product runs.</b> <see cref="CordWorkspace"/> holds
/// the scope rule, the refusal semantics and the composed draft; this command supplies the two
/// things the engine deliberately does not have — where source lives, and what "accept" means.</para>
///
/// <para><b>What "accept" means HERE.</b> CordyOSS rule 10 says an operation may write to a candidate
/// and only an explicit user action writes to accepted source. In a Git checkout the working tree IS
/// the candidate: this command writes there, a person reads <c>git diff</c>, and staging or
/// committing is the acceptance — which is §4a's table read literally. A second shadow
/// <c>.cord.candidate/</c> tree would be a weaker version control system sitting on top of a
/// stronger one, and the reviewer would have to learn both.</para>
///
/// <para><b>Nothing is written unless the result is COHERENT.</b> A refused change leaves every file
/// byte-identical, which is the acceptance criterion from CordyOSS Phase 1 and the property that lets
/// an agent retry without first repairing the damage from its last attempt.</para>
/// </summary>
public static class ApplyCommand
{
    public static int Run(Args args, Output output)
    {
        if (args.Value("scope") is not { Length: > 0 } scopeText)
            return output.Usage("--scope is required: the one aggregate this change may touch, "
                + $"e.g. --scope domain, --scope screen:claims. Kinds: {string.Join(", ", CordAggregateKinds.All)}");

        if (ParseScope(scopeText) is not { } scope)
            return output.Usage($"'{scopeText}' is not an aggregate — expected <kind> or <kind>:<key>, "
                + $"where kind is one of {string.Join(", ", CordAggregateKinds.All)}");

        var selection = Selection.Resolve(args, output, out var exit);
        if (selection is null) return exit;

        if (selection.Apps.Count != 1)
        {
            return output.Usage(selection.Apps.Count == 0
                ? "this workspace has no apps — run `cordango add app <name>` first"
                : "--app is required: a change belongs to exactly one app "
                    + $"({string.Join(", ", selection.Apps.Select(a => a.Key))})");
        }

        var loaded = selection.Apps[0];
        if (!loaded.Ok)
        {
            // Including the case where a model DID assemble but a file was refused: authoring on top
            // of a partially-read app would rewrite the whole tree from a baseline missing whatever
            // was dropped, which turns a one-file problem into a silent deletion.
            return output.Fail($"{loaded.Key} could not be read as source, so there is nothing to change",
                loaded.Problems);
        }

        if (ReadOps(args, output) is not { } ops) return ExitCodes.Usage;

        // The scope check lives in CordWorkspace, not here. Re-deriving "does this operation belong"
        // in the CLI would be a second implementation of CordAggregates.Admits, free to disagree with
        // the hosted one about what an aggregate contains.
        var workspace = new CordWorkspace(loaded.App, null, scope);
        var change = workspace.Apply(ops);

        if (!change.Ok)
        {
            return output.Fail($"refused — {loaded.Key} is unchanged",
                change.Errors.Select(AppFolder.Describe),
                new JsonObject { ["app"] = loaded.Key, ["scope"] = scope.ToString(), ["written"] = new JsonArray() });
        }

        // COHERENCE decides whether the change may be kept — see CordTransaction's two thresholds.
        // Cord having read the operations is emphatically not enough: CordCheck omits nearly every
        // gate rule, so accepting on its word would let a gate-invalid change become the thing the
        // next operation edits.
        var report = Pipeline.Check(change.NextCandidate!, loaded.Key, loaded.Path, change.Map);
        if (!report.Coherent)
        {
            return output.Fail($"refused — the result does not hold together, {loaded.Key} is unchanged",
                report.Errors,
                new JsonObject { ["app"] = loaded.Key, ["scope"] = scope.ToString(), ["written"] = new JsonArray() });
        }

        // No "can this be written" guard, and that is the point of CordSource: it is TOTAL, so
        // there is no state in which an app is valid and its files would be incomplete. The writer
        // this replaced could not express a raw fragment and so could not write a single one of the
        // 15 reference apps.
        var source = CordSource.Write(change.NextCandidate!);

        var dryRun = args.Has("dry-run");
        var diff = dryRun ? AppFolder.Diff(loaded, source) : AppFolder.Save(loaded, source);

        return output.Ok(
            new JsonObject
            {
                ["app"] = loaded.Key,
                ["scope"] = scope.ToString(),
                ["dryRun"] = dryRun,
                ["written"] = new JsonArray([.. diff.Written.Select(p => (JsonNode)p!)]),
                ["deleted"] = new JsonArray([.. diff.Deleted.Select(p => (JsonNode)p!)]),
                ["check"] = report.ToJson(),
            },
            w =>
            {
                var verb = dryRun ? "would write  " : "wrote   ";
                foreach (var path in diff.Written) w.WriteLine($"  {verb}{path}");
                foreach (var path in diff.Deleted) w.WriteLine($"  {(dryRun ? "would delete " : "deleted ")}{path}");
                if (diff.Empty)
                    w.WriteLine("  applied, but the files already said exactly this");

                // Said on the success path, not only on failure: an author who has just been told
                // "ok" needs to know the app is not finished yet, and finding out at publish time is
                // finding out too late.
                if (!report.Valid)
                    w.WriteLine($"  {loaded.Key} is coherent but not yet a finished app "
                        + $"({report.Errors.Count} still needed — run `cordango check`)");
            });
    }

    /// <summary><c>domain</c>, <c>screen:claims</c>, <c>tab:claims/inbox</c>. The key may itself
    /// contain a slash (a tab is <c>screen/tab</c>), so only the FIRST colon separates.</summary>
    internal static CordAggregateRef? ParseScope(string text)
    {
        var colon = text.IndexOf(':');
        var kind = colon < 0 ? text : text[..colon];
        var key = colon < 0 ? null : text[(colon + 1)..];

        if (!CordAggregateKinds.All.Contains(kind, StringComparer.Ordinal)) return null;
        if (key is { Length: 0 }) return null;

        return new CordAggregateRef(kind, key);
    }

    /// <summary>
    /// The operations, from a file or from stdin when the path is <c>-</c>.
    ///
    /// <para>Both <c>{"ops":[…]}</c> and a bare <c>[…]</c> are accepted. The first is what a
    /// <c>.cordango</c> file looks like, so somebody will copy one; the second is what
    /// <see cref="CordOps.Parse"/> actually wants. Refusing either would be a papercut with no
    /// safety argument behind it.</para>
    /// </summary>
    private static JsonArray? ReadOps(Args args, Output output)
    {
        var source = args.First ?? args.Value("ops");
        if (string.IsNullOrEmpty(source))
        {
            output.Usage("give a file of operations, or `-` to read them from stdin: "
                + "cordango apply ops.json --app <key> --scope <kind>");
            return null;
        }

        string text;
        try
        {
            text = source == "-" ? Console.In.ReadToEnd() : AtomicFile.Read(source);
        }
        catch (IOException ex)
        {
            output.Usage($"{source}: unreadable ({ex.Message})");
            return null;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            output.Usage($"{source}: not valid JSON ({ex.Message})");
            return null;
        }

        var ops = parsed switch
        {
            JsonArray array => array,
            JsonObject obj when obj["ops"] is JsonArray inner => inner,
            _ => null,
        };

        if (ops is null)
        {
            output.Usage($"{source}: expected an array of operations, or an object with an `ops` array");
            return null;
        }

        // Detached: JsonNode refuses to be re-parented, and CordWorkspace.Apply reads it as a
        // free-standing node.
        return (JsonArray)ops.DeepClone();
    }
}
