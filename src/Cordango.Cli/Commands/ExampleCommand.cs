// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// What a correctly written one LOOKS like — the sibling of <see cref="VocabularyCommand"/>.
///
/// <para><b>Why a second command rather than more output from the first.</b> They answer different
/// questions. <c>vocabulary</c> answers "what may be written": the schema, the contract, the
/// exhaustive list of properties and their types. <c>example</c> answers "what does a right one
/// look like", and the gap between the two is exactly where authoring mistakes live. Six real ones
/// were made building the application these examples come from, every one against a careful
/// reading of the schema:</para>
///
/// <list type="bullet">
/// <item>an empty <c>blocks: []</c> used as a spacer — the array's <c>minItems</c> is 1</item>
/// <item><c>cols</c> on a form block, where the property is <c>columns</c> and capped at 2</item>
/// <item><c>limit</c> on a repeat, where it belongs on the repeat's <c>source</c></item>
/// <item>a <c>stat</c> with a <c>field</c> on a page, which has no row to read it from</item>
/// <item>a single-field <c>unique</c> combination, where the field takes <c>unique: true</c></item>
/// <item><c>format: "percent"</c> on a ratio — it appends a sign, it does not scale</item>
/// </list>
///
/// <para>Every one is legal-looking, none is caught by reading the properties, and all six are
/// obvious beside a working instance. So: keep the schema answer, add the worked one.</para>
///
/// <para><b>The example is not written here.</b> It is extracted from a shipped application that
/// passes the gate. A snippet maintained by hand is a copy, and a copy of a format goes stale the
/// first time somebody renames a property; one taken from a real app cannot, because the rename
/// breaks the app it was taken from.</para>
/// </summary>
public static class ExampleCommand
{
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
        var asked = args.First;
        if (string.IsNullOrWhiteSpace(asked)) return List(output);

        // An authoring name is what the model writes and what a diagnostic quotes, so accept
        // either spelling: `data.gantt` and `gantt` are the same question.
        var kind = asked.Contains('.', StringComparison.Ordinal)
            ? BlockKinds.Find(asked)?.Canonical ?? asked[(asked.IndexOf('.') + 1)..]
            : asked;

        if (Examples.For(kind) is not { } example) return NotFound(kind, asked, output);

        var authoring = BlockKinds.ByCanonical.TryGetValue(kind, out var names)
            ? string.Join(" | ", names)
            : null;
        var component = ComponentCatalog.Find("block." + kind);
        // The component catalog covers most kinds and not all — gantt, board, orgchart and the
        // hand-placed ones have no entry. The schema always has something to say about a kind, so
        // it is the fallback rather than printing an example with no claim attached to it.
        var description = component?.Description ?? SchemaDescription(kind);

        var payload = new JsonObject
        {
            ["kind"] = kind,
            ["authoringName"] = authoring,
            ["description"] = description,
            ["whenToUse"] = component?.WhenToUse,
            ["from"] = $"{Examples.Source}, {example.From}",
            ["example"] = example.Block.DeepClone(),
        };

        return output.Ok(payload, w =>
        {
            w.WriteLine($"{component?.Label ?? kind} — a worked example");
            w.WriteLine();
            if (authoring is not null) w.WriteLine($"  written as   {authoring}");
            w.WriteLine($"  taken from   {Examples.Source}, {example.From}");
            w.WriteLine();
            if (description is { Length: > 0 } d) w.WriteLine(Wrap(d, "  "));
            if (component?.WhenToUse is { Length: > 0 } u)
            {
                w.WriteLine();
                w.WriteLine(Wrap("When to use it: " + u, "  "));
            }
            w.WriteLine();
            w.WriteLine(JsonSerializer.Serialize(example.Block, Pretty));
            w.WriteLine();
            w.WriteLine($"  every property it accepts:  cordango vocabulary block {kind}");
        });
    }

    private static int List(Output output)
    {
        var byFamily = new JsonObject();
        foreach (var family in BlockKinds.Families)
        {
            var kinds = KindsIn(family);
            if (kinds.Count > 0)
                byFamily[family] = new JsonArray([.. kinds.Select(k => (JsonNode)k!)]);
        }
        var byHand = Examples.Kinds.Where(BlockKinds.NotAuthorable.Contains).ToList();
        if (byHand.Count > 0)
            byFamily["byHand"] = new JsonArray([.. byHand.Select(k => (JsonNode)k!)]);

        var payload = new JsonObject { ["source"] = Examples.Source, ["kinds"] = byFamily };

        return output.Ok(payload, w =>
        {
            w.WriteLine("A worked example of each block kind, taken from a shipped application that");
            w.WriteLine("passes the gate. `cordango vocabulary` says what one ACCEPTS; this says what");
            w.WriteLine("a right one looks like.");
            w.WriteLine();
            w.WriteLine("  cordango example <kind>        e.g. cordango example gantt");
            w.WriteLine("  cordango example data.gantt    the authoring name works too");
            w.WriteLine();
            foreach (var family in BlockKinds.Families)
            {
                var kinds = KindsIn(family);
                if (kinds.Count > 0) w.WriteLine($"  {family,-10} {string.Join(", ", kinds)}");
            }
            if (byHand.Count > 0)
            {
                w.WriteLine();
                w.WriteLine($"  by hand    {string.Join(", ", byHand)}");
                w.WriteLine("             (legal in a stored definition, never offered to the model)");
            }
        });
    }

    /// <summary>What the SCHEMA says a kind is, for the kinds the component catalog has no entry
    /// for. Trimmed to its first two sentences: the whole description is sometimes a page of
    /// reasoning, and an example is not the place to reprint it.</summary>
    private static string? SchemaDescription(string kind)
    {
        var defs = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)?["$defs"] as JsonObject;
        if (defs?["block_" + kind] is not JsonObject def) return null;
        var text = def["description"]?.GetValue<string>()
                   ?? (def["properties"] as JsonObject)?["kind"]?["description"]?.GetValue<string>();
        if (text is null) return null;

        var parts = text.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        var head = string.Join(". ", parts.Take(2)).Trim();
        return head.EndsWith('.') ? head : head + ".";
    }

    private static List<string> KindsIn(string family) =>
        [.. BlockKinds.NamesFor(family)
            .Select(n => BlockKinds.Find(n)!.Canonical)
            .Distinct(StringComparer.Ordinal)
            .Where(k => Examples.For(k) is not null)];

    private static int NotFound(string kind, string asked, Output output)
    {
        // A kind that EXISTS but has no example is a different answer from one that does not
        // exist, and conflating them sends somebody hunting for a typo they did not make.
        if (BlockKinds.NotAuthorable.Contains(kind) || BlockKinds.ByCanonical.ContainsKey(kind))
            return output.Fail($"'{kind}' is a real block kind, but no example ships for it", [
                "It is absent from the application the examples come from, on purpose: `widgets` "
                + "is deprecated at authoring, and `externalEmbed` needs a provider a platform "
                + "admin has approved before anything can render it.",
                $"Its contract is still there: cordango vocabulary block {kind}",
            ]);

        var near = Examples.Kinds
            .Where(k => k.Contains(kind, StringComparison.OrdinalIgnoreCase)
                        || kind.Contains(k, StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();
        var hints = new List<string>();
        if (near.Count > 0) hints.Add("Did you mean: " + string.Join(", ", near) + "?");
        hints.Add("Everything with an example: cordango example");
        return output.Fail($"no block kind called '{asked}'", hints, code: ExitCodes.Usage);
    }

    /// <summary>Hard-wrap prose at a width a terminal and a transcript both read comfortably.</summary>
    private static string Wrap(string text, string indent, int width = 76)
    {
        var lines = new List<string>();
        var line = indent;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width && line.Length > indent.Length)
            {
                lines.Add(line);
                line = indent;
            }
            line += (line.Length > indent.Length ? " " : "") + word;
        }
        if (line.Length > indent.Length) lines.Add(line);
        return string.Join("\n", lines);
    }
}
