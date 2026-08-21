// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cordango.Definition;

/// <summary>
/// Last-resort deterministic recovery for an exhausted repair loop. Motivated live (2026-07-19
/// survey run): the best candidate was a genuinely correct forms app whose ONLY error was a
/// schedule workflow missing its `cron` — and the run died over it, because every model "repair"
/// is a full regeneration that fixes one thing and breaks another. When the remaining errors are
/// confined to DROPPABLE structures (workflows, commands), removing those structures beats
/// failing the whole run: an app missing one reminder automation is useful; no app is not.
/// The caller re-gates the result and reports what was dropped — nothing is silent.
/// </summary>
public static class Salvage
{
    private static readonly Regex WorkflowKey = new(@"workflow '([a-z][a-z0-9_]*)'", RegexOptions.Compiled);
    private static readonly Regex CommandKey = new(@"command '([a-z][a-z0-9_]*)'", RegexOptions.Compiled);
    private static readonly Regex WorkflowIndex = new(@"/workflows/(\d+)", RegexOptions.Compiled);

    /// <summary>Drop the workflows/commands the errors blame, on a clone. Returns (null, [])
    /// when ANY error points at something that is not a droppable structure — salvage must never
    /// paper over a broken entity, field, process or view. The caller must re-gate the result:
    /// this method only removes; it does not prove the remainder clean.</summary>
    public static (JsonNode? Doc, List<string> Dropped) TryDropFailingStructures(
        JsonNode? doc, IReadOnlyList<string> errors)
    {
        if (doc is not JsonObject || errors.Count == 0) return (null, new());

        var workflowKeys = new HashSet<string>();
        var workflowIndexes = new HashSet<int>();
        var commandKeys = new HashSet<string>();
        foreach (var e in errors)
        {
            var matched = false;
            foreach (Match m in WorkflowKey.Matches(e)) { workflowKeys.Add(m.Groups[1].Value); matched = true; }
            foreach (Match m in CommandKey.Matches(e)) { commandKeys.Add(m.Groups[1].Value); matched = true; }
            foreach (Match m in WorkflowIndex.Matches(e)) { workflowIndexes.Add(int.Parse(m.Groups[1].Value)); matched = true; }
            if (!matched) return (null, new());   // an error we cannot attribute → not salvageable
        }

        var clone = (JsonObject)doc.DeepClone();
        var dropped = new List<string>();

        if (clone["workflows"] is JsonArray workflows)
        {
            for (var i = workflows.Count - 1; i >= 0; i--)
            {
                var key = (workflows[i] as JsonObject)?["key"]?.GetValue<string>();
                if (!workflowIndexes.Contains(i) && (key is null || !workflowKeys.Contains(key))) continue;
                workflows.RemoveAt(i);
                dropped.Add($"workflow '{key ?? $"#{i}"}'");
            }
        }

        if (commandKeys.Count > 0)
        {
            if (clone["commands"] is JsonArray commands)
                for (var i = commands.Count - 1; i >= 0; i--)
                {
                    var key = (commands[i] as JsonObject)?["key"]?.GetValue<string>();
                    if (key is null || !commandKeys.Contains(key)) continue;
                    commands.RemoveAt(i);
                    dropped.Add($"command '{key}'");
                }
            // A dropped command leaves dangling references the gate would reject: unbind the
            // transitions that named it (the runtime synthesizes a command for an unbound
            // transition) and strip it from role grants.
            foreach (var pn in (clone["processes"] as JsonArray) ?? new JsonArray())
                foreach (var tn in ((pn as JsonObject)?["transitions"] as JsonArray) ?? new JsonArray())
                    if (tn is JsonObject t && t["command"]?.GetValue<string>() is { } tc && commandKeys.Contains(tc))
                        t.Remove("command");
            foreach (var rn in (clone["roles"] as JsonArray) ?? new JsonArray())
                foreach (var gn in ((rn as JsonObject)?["grants"] as JsonArray) ?? new JsonArray())
                    if (gn is JsonObject g && g["commands"] is JsonArray gc)
                        for (var i = gc.Count - 1; i >= 0; i--)
                            if (gc[i]?.GetValue<string>() is { } ck && commandKeys.Contains(ck))
                                gc.RemoveAt(i);
        }

        return dropped.Count == 0 ? (null, new()) : (clone, dropped);
    }
}
