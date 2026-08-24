// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// What may be written, one construct at a time.
///
/// <para><b>Why this exists, and it is not a convenience.</b> A live agent working in a cordango
/// workspace went looking for the setting names a calendar view accepts, found no way to ask, and
/// resorted to scraping strings out of the 78 MB <c>cordango.exe</c> to recover the embedded JSON Schema.
/// It got the right answer — <c>startField</c>, <c>endField</c>, <c>colorField</c> are real — by the
/// most fragile route available, and it was heading for the whole 143 KB schema next.</para>
///
/// <para><b>That was the correct inference from its situation.</b> Cord models the domain and does
/// not yet model screens, so <c>views/</c> files hold raw App Definition block trees with no semantic
/// vocabulary behind them. In that half of the format the App Definition schema genuinely is the only
/// specification there is, and the workspace contained no copy of it. The agent did not misbehave; it
/// filled a hole.</para>
///
/// <para><b>Scoped on demand, never the whole thing.</b> CordyOSS §8.3 says the model must not
/// receive the complete schema — it does not say the model should have to guess.
/// <c>block_calendar</c> is 5 KB against the schema's 143 KB, so answering one question costs 3.5% of
/// the document that layer exists to avoid. References are NAMED rather than inlined, which keeps the
/// answer small and teaches the discovery loop instead of flattening it.</para>
/// </summary>
public static class VocabularyCommand
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Where one idea wears two names, listed because an agent found every one of these by trial and
    /// error and said so.
    ///
    /// <para>An operation is what a model SENDS; a file is what gets WRITTEN. They diverged for
    /// defensible reasons at each step — <c>target</c> is shorter than <c>targetEntity</c> — and the
    /// sum is a seam nobody documented. Naming the pairs is the cheap fix; collapsing them is a
    /// breaking change to both surfaces and wants its own decision.</para>
    ///
    /// <para><b>A table of names has to be right or it is worse than absent.</b> Two rows here said
    /// a calculation is written <c>calculate.aggregate</c> and <c>calculate.expression</c>. There is
    /// no <c>calculate</c> key anywhere in the format — the word appeared only in this table — and
    /// the file spelling is <c>computed.rollup</c> and <c>computed.expr</c>. An agent following the
    /// document written to stop it guessing spent a round trip guessing anyway, which is the exact
    /// failure this list exists to prevent. <c>VocabularyTests</c> now checks every File value
    /// against the schema.</para>
    /// </summary>
    /// <summary>
    /// The value tokens, and — the part nobody could find — the fact that the SAME ones work in an
    /// effect as in a filter.
    ///
    /// <para>They are documented in the App Definition schema per property, which means an author
    /// reading about effects saw <c>{{today}}</c> and <c>{{now}}</c> and had no way to learn that
    /// the offsets they had already used in a filter were also allowed there. One agent shipped a
    /// recurrence feature with a note saying the arithmetic form was the one thing they could not
    /// verify. It was allowed, and it was also broken in the runtime — see
    /// <c>ValueTokens</c> — so the answer they could not find was "yes, and it does not work".</para>
    ///
    /// <para><b>Here rather than in the ops schema.</b> That schema is what a model reads on every
    /// request and it is a few bytes under a deliberate ceiling; the standing rule is to narrow
    /// before raising one. This command is the documentation surface and costs nothing per
    /// request.</para>
    /// </summary>
    private static readonly (string Token, string Means)[] Tokens =
    [
        ("{{actor.id}}", "the signed-in PERSON — what a 'mine' filter and an owner stamp both compare"),
        ("{{today}}", "the current date, yyyy-MM-dd"),
        ("{{now}}", "the current instant, ISO 8601"),
        ("{{today+7}}", "an offset off either anchor; the unit defaults to days"),
        ("{{today-30d}} {{today+2w}}", "days and weeks"),
        ("{{now-4h}}", "hours, on the instant anchor only — a date has no hours"),
        ("{{record.<field>}}", "a field of the record the rule is about (effects and templates)"),
        ("{{source.<field>}}", "a field of the row being iterated, inside createForEach"),
    ];

    /// <summary>What the offsets deliberately cannot say, with the reason — an absence with a reason
    /// is a closed question, and a bare one gets reopened by every author who wants a monthly
    /// anything.</summary>
    private const string TokenLimits =
        "Units are d, w and h. There is no month or year offset: JavaScript's setMonth overflows "
        + "(Jan 31 + 1m = Mar 3) and .NET's AddMonths clamps (Feb 28), so {{today+1m}} would select "
        + "different rows in the browser than on the server. For monthly and yearly steps use a "
        + "createForEach over a `range` source, whose `step` takes day/week/month/year.";

    public static readonly (string Idea, string Operation, string File)[] Seams =
    [
        ("a reference's target", "target", "targetEntity"),
        ("an automation trigger", "on", "trigger"),
        ("a rollup", "aggregate", "computed.rollup"),
        ("an expression", "expr", "computed.expr"),
        ("a section's entity", "of", "of (screens) / entity (views)"),
        ("a board view", "board", "kind: kanban"),
        ("an entity's plural", "labelPlural", "plural"),
        ("an entity's display field", "displayField", "display"),
    ];

    public static int Run(Args args, Output output)
    {
        var schema = Schemas.AppDefinitionSchemaNode() as JsonObject;
        var defs = schema?["$defs"] as JsonObject;
        if (defs is null) return output.Fail("the App Definition schema could not be read", []);

        // The OPERATION vocabulary is a different question from the App Definition's, and it was the
        // one nobody could ask. An agent discovered it by submitting deliberately-invalid operations
        // and reading the rejections — the same reverse-engineering the binary scrape was, one layer
        // up. The schemas existed the whole time; nothing exposed them.
        if (args.Positional.Count > 0 && args.Positional[0] is "operation" or "op")
            return Operations([.. args.Positional.Skip(1)], output);

        // `cordango vocabulary block calendar` → block_calendar. Joining the words is what makes the
        // command read like a question rather than like a lookup key.
        var name = string.Join('_', args.Positional);

        // ...and the same join turns `cordango vocabulary core organizations` into a core app's system key,
        // which is exactly the string a reference has to carry. Checked before the $defs lookup because
        // a core app is not an App Definition construct and would otherwise fall through to "no such
        // vocabulary entry" — the answer that sent one agent off to declare its own organization.
        if (CoreAppRegistry.Find(name) is { } core) return Core(core, output);

        return name.Length == 0 ? Index(defs, output) : One(defs, name, output);
    }

    /// <summary>
    /// What one core app holds, and how to point at it.
    ///
    /// <para>Field keys are listed for every entity: a reference needs only the entity key, but the
    /// author's next question is always whether the thing they were about to declare is already a
    /// column on the canonical record. Answering both at once is what stops the second lookup becoming
    /// a second entity.</para>
    /// </summary>
    private static int Core(CoreApp core, Output output)
    {
        var entities = new JsonArray([.. core.Entities.Select(e => (JsonNode)new JsonObject
        {
            ["key"] = e.Key,
            ["label"] = e.Label,
            ["description"] = e.Description,
            ["fields"] = Words(e.FieldKeys),
        })]);

        return output.Ok(
            new JsonObject
            {
                ["systemKey"] = core.SystemKey,
                ["name"] = core.Name,
                ["entities"] = entities,
                ["reference"] = new JsonObject
                {
                    ["type"] = "reference",
                    ["targetApp"] = core.SystemKey,
                    ["target"] = "<one of the entity keys above>",
                },
            },
            w =>
            {
                w.WriteLine($"{core.Name} ({core.SystemKey}) — provided by the platform to every workspace.");
                w.WriteLine("Reference it; do not declare your own copy.");
                w.WriteLine();

                foreach (var entity in core.Entities)
                {
                    w.WriteLine($"  {entity.Key}  ({entity.Label})");
                    if (entity.Description is { Length: > 0 } description)
                        w.WriteLine($"    {description}");
                    w.WriteLine($"    fields: {string.Join(", ", entity.FieldKeys)}");
                    w.WriteLine();
                }

                w.WriteLine("On a field of your own entity:");
                w.WriteLine($"  type: reference   targetApp: {core.SystemKey}   target: <entity key>");
            });
    }

    /// <summary>
    /// What <c>cordango apply</c> accepts — the authority for an operation, as opposed to the App
    /// Definition schema which is the authority for a lowered document.
    /// </summary>
    private static int Operations(IReadOnlyList<string> rest, Output output)
    {
        if (rest.Count == 0)
        {
            return output.Ok(
                new JsonObject
                {
                    ["domain"] = Words(CordOps.DomainOpNames),
                    ["behaviour"] = Words(CordOps.BehaviourOpNames),
                    ["ui"] = Words(CordOps.UiOpNames),
                },
                w =>
                {
                    w.WriteLine("Operations `cordango apply` accepts, by the scope that offers them:");
                    w.WriteLine($"  --scope domain      {string.Join(", ", CordOps.DomainOpNames)}");
                    w.WriteLine($"  --scope behaviour   {string.Join(", ", CordOps.BehaviourOpNames)}");
                    w.WriteLine($"  --scope access      upsert_role, remove_behaviour");
                    w.WriteLine($"  --scope screen:<k>  {string.Join(", ", CordOps.UiOpNames)}");
                    w.WriteLine();
                    w.WriteLine("  cordango vocabulary operation upsert_field");
                });
        }

        var name = rest[0];
        if (CordOps.SchemaFor(name) is not { } schema)
        {
            var near = CordOps.AllOpNames
                .Where(o => o.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return output.Fail($"no operation '{name}'",
                near.Count > 0
                    ? [$"did you mean: {string.Join(", ", near)}"]
                    : [$"operations: {string.Join(", ", CordOps.AllOpNames)}"]);
        }

        return output.Ok(
            new JsonObject { ["operation"] = name, ["schema"] = schema.DeepClone() },
            w => w.WriteLine(schema.ToJsonString(Pretty)));
    }

    /// <summary>
    /// What exists, and — more usefully — which half of the format each thing belongs to.
    ///
    /// <para>Cord's own words come FIRST because they are what an entity, lifecycle or role file is
    /// written in, and they are small enough to read whole. The App Definition constructs come second
    /// because they are what the screen and view files still are.</para>
    /// </summary>
    private static int Index(JsonObject defs, Output output)
    {
        var blocks = defs.Select(d => d.Key)
            .Where(k => k.StartsWith("block_", StringComparison.Ordinal))
            .Select(k => k["block_".Length..])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var constructs = defs.Select(d => d.Key)
            .Where(k => !k.StartsWith("block_", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var cord = new JsonObject
        {
            ["sectionKinds"] = Words(CordSectionKinds.All),
        };
        foreach (var map in CordVocabulary.All) cord[Camel(map.Name)] = Words(map.Words);

        // What the platform has and Cord deliberately does not, WITH the reason. An absence with a
        // reason attached is a closed question; a bare absence is one every author reopens, and
        // three separate agent reports reopened one each.
        var withheld = new JsonObject();
        foreach (var map in CordVocabulary.All.Where(m => m.Withheld.Count > 0))
        {
            var reasons = new JsonObject();
            foreach (var (word, because) in map.Withheld) reasons[word] = because;
            withheld[Camel(map.Name)] = reasons;
        }

        // The platform's own apps. Not a vocabulary in the CordWordMap sense — but the index is where
        // an author looks to find out what exists, and a concept that exists and cannot be found here
        // gets declared a second time by whoever needed it.
        var coreApps = new JsonObject();
        foreach (var core in CoreAppRegistry.All)
            coreApps[core.SystemKey] = Words(core.Entities.Select(e => e.Key));

        return output.Ok(
            new JsonObject
            {
                ["cord"] = cord,
                ["coreApps"] = coreApps,
                ["withheld"] = withheld,
                ["blocks"] = Words(blocks),
                ["constructs"] = Words(constructs),
                ["tokens"] = new JsonObject
                {
                    ["values"] = new JsonObject(
                        Tokens.Select(t => KeyValuePair.Create(t.Token, (JsonNode?)t.Means))),
                    ["limits"] = TokenLimits,
                    ["where"] = "The same tokens resolve in a filter value, a condition value, an "
                        + "effect's set, and a notification's text.",
                },
                ["usage"] = "cordango vocabulary <name>  ·  cordango vocabulary block <kind>",
            },
            w =>
            {
                w.WriteLine("Cord's own words — what entity, lifecycle, role and screen files are written in:");
                foreach (var (key, value) in cord)
                    w.WriteLine($"  {key,-18} {string.Join(", ", (value as JsonArray ?? []).Select(v => (string?)v))}");

                w.WriteLine();
                w.WriteLine("Core apps — the platform already provides these. Reference them from your");
                w.WriteLine("own entities (targetApp), never declare a second copy:");
                foreach (var core in CoreAppRegistry.All)
                    w.WriteLine($"  {core.SystemKey,-20} {string.Join(", ", core.Entities.Select(e => e.Key))}");

                w.WriteLine();
                w.WriteLine("App Definition blocks — what views/ and screens/ files still contain:");
                w.WriteLine("  " + string.Join(", ", blocks));

                w.WriteLine();
                w.WriteLine("Other constructs:");
                w.WriteLine("  " + string.Join(", ", constructs));

                if (withheld.Count > 0)
                {
                    w.WriteLine();
                    w.WriteLine("DELIBERATELY NOT OFFERED — the platform has these, Cord does not, and why:");
                    foreach (var (vocabulary, reasons) in withheld)
                        foreach (var (word, because) in reasons as System.Text.Json.Nodes.JsonObject ?? [])
                            w.WriteLine($"  {vocabulary}.{word}: {(string?)because}");
                }

                w.WriteLine();
                w.WriteLine("VALUE TOKENS. The same ones resolve in a filter value, a condition value,");
                w.WriteLine("an effect's set and a notification's text — there is not a narrower set for effects:");
                foreach (var (token, means) in Tokens)
                    w.WriteLine($"  {token,-26} {means}");
                w.WriteLine();
                foreach (var line in Wrap(TokenLimits, 92)) w.WriteLine($"  {line}");

                w.WriteLine();
                w.WriteLine("THE SAME IDEA HAS DIFFERENT NAMES IN DIFFERENT LAYERS. Known pairs:");
                foreach (var (idea, operation, file) in Seams)
                    w.WriteLine($"  {idea,-25} operation: {operation,-16} file: {file}");

                w.WriteLine();
                w.WriteLine("  cordango vocabulary operation          what `cordango apply` accepts");
                w.WriteLine("  cordango vocabulary block calendar     what a calendar block accepts");
                w.WriteLine("  cordango vocabulary field              a field's properties and types");
                w.WriteLine("  cordango vocabulary core organizations what a core app holds");
            });
    }

    /// <summary>Fold a paragraph to a width, so a terminal renders it as prose rather than as one
    /// line somebody has to scroll.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    private static int One(JsonObject defs, string name, Output output)
    {
        if (defs[name] is not JsonObject entry)
        {
            var near = defs.Select(d => d.Key)
                .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .ToList();

            return output.Fail($"no vocabulary entry '{name}'",
                near.Count > 0
                    ? [$"did you mean: {string.Join(", ", near)}"]
                    : ["run `cordango vocabulary` for the index"]);
        }

        // Referenced names are LISTED, not inlined. Inlining is how a 5 KB answer becomes the whole
        // 143 KB schema in three hops, which is the thing this command exists to prevent.
        var refs = new SortedSet<string>(StringComparer.Ordinal);
        Collect(entry, refs);
        refs.Remove(name);

        var text = entry.ToJsonString(Pretty);

        return output.Ok(
            new JsonObject
            {
                ["name"] = name,
                ["schema"] = entry.DeepClone(),
                ["references"] = Words(refs),
            },
            w =>
            {
                w.WriteLine(text);
                if (refs.Count == 0) return;

                w.WriteLine();
                w.WriteLine("References — each is its own question:");
                foreach (var reference in refs) w.WriteLine($"  cordango vocabulary {reference}");
            });
    }

    private static void Collect(JsonNode? node, SortedSet<string> refs)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key == "$ref" && (string?)value is { } target
                        && target.StartsWith("#/$defs/", StringComparison.Ordinal))
                        refs.Add(target["#/$defs/".Length..]);
                    else Collect(value, refs);
                }
                return;

            case JsonArray a:
                foreach (var item in a) Collect(item, refs);
                return;
        }
    }

    private static JsonArray Words(IEnumerable<string> words) =>
        new([.. words.Select(w => (JsonNode)w)]);

    /// <summary>"button style" → "buttonStyle", so the JSON keys are usable identifiers.</summary>
    private static string Camel(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? name
            : parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
