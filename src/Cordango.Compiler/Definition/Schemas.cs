// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Cordango.Definition;

/// <summary>Loads the embedded App Definition schema + canonical platform entities
/// (the single source of truth, shared with the Python spec).</summary>
/// <summary>
/// Which App Definition contract a document is written against.
///
/// <para>The value existed in thirteen reference apps and nowhere else — no constant, no schema
/// rule, and a runtime check spelled <c>StartsWith("2.")</c> buried in an access resolver. So when
/// the design agent stopped being shown those apps, it wrote the only version a brand-new document
/// suggests: <c>"1.0"</c>. It passed the gate (the pattern is just <c>^\d+\.\d+$</c>), compiled
/// cleanly, saved, and produced an app the runtime refused to open — "App was built against an older
/// schema" for an app built one minute earlier (live 2026-08-05).</para>
///
/// <para>It is not a design decision, so nothing asks a model for it: the platform stamps it. This
/// constant is the one statement of it.</para>
/// </summary>
public static class AppSchemaVersion
{
    public const string Current = "2.0";

    /// <summary>The major line the runtime can actually serve. A 1.x manifest predates commands,
    /// processes and the record_state rename — its tables do not match.</summary>
    public const string SupportedMajor = "2.";

    public static bool IsSupported(string? version) =>
        version is not null && version.StartsWith(SupportedMajor, StringComparison.Ordinal);

    /// <summary>
    /// Writes the current version onto a definition, returning what it said before if that differed.
    ///
    /// <para><b>Every path that compiles a definition must call this</b>, and there are more of them
    /// than there look: the design agent, the build endpoint, import. Stamping in only one of them
    /// produced a genuinely absurd state — an "Update the app" button that rebuilt the same wrong
    /// version into the same unservable manifest, so pressing it did nothing, correctly, forever
    /// (live 2026-08-05).</para>
    ///
    /// <para>Safe precisely because a document reaching a compiler has already passed the CURRENT
    /// gate. Anything that validates against today's schema is a document of today's version; a
    /// field claiming otherwise is a mislabel, not a translation problem.</para>
    /// </summary>
    public static string? Stamp(JsonNode? definition)
    {
        if (definition is not JsonObject doc) return null;
        // Read defensively — the value may be a number, and GetValue<string> on that throws.
        var declared = doc["schemaVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (declared == Current) return null;

        var had = doc.ContainsKey("schemaVersion");
        doc["schemaVersion"] = Current;
        return had ? declared ?? "a non-string value" : null;
    }
}

public static class Schemas
{
    private static readonly Assembly Asm = typeof(Schemas).Assembly;

    public static string LoadResource(string logicalName)
    {
        using var stream = Asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"embedded resource '{logicalName}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static readonly string AppDefinitionSchemaJson = LoadResource("app-definition.schema.json");

    public static readonly JsonSchema AppDefinitionSchema = JsonSchema.FromText(AppDefinitionSchemaJson);

    /// <summary>The App Definition JSON Schema as a JsonNode — used as the tool input_schema
    /// that forces the model's structured output.</summary>
    public static JsonNode AppDefinitionSchemaNode() => JsonNode.Parse(AppDefinitionSchemaJson)!;

    /// <summary>The BLUEPRINT schema — a different language from the App Definition. Proposer tool
    /// schemas are sliced from this and never from the definition schema, so a proposer is
    /// structurally incapable of emitting definition structures and undoing an approval.</summary>
    public static readonly string BlueprintSchemaJson = LoadResource("blueprint.schema.json");

    public static JsonNode BlueprintSchemaNode() => JsonNode.Parse(BlueprintSchemaJson)!;

    /// <summary>A standalone schema for ONE part of a blueprint — <c>#/$defs/&lt;def&gt;</c>, or a
    /// named top-level property — with <c>$defs</c> pruned to what that part actually reaches. This
    /// is how each interview role gets a tool schema covering exactly its own layer: a concept
    /// proposer handed the whole blueprint schema could emit workflows and screens the user never
    /// saw.
    /// <para>The <c>$id</c> is dropped for the same reason <see cref="PageSchema"/> drops it —
    /// compiling a derived schema that inherits a registered id throws.</para>
    /// <para>The def is INLINED at the root rather than referenced with a bare <c>$ref</c>. This is
    /// a tool <c>input_schema</c>, and the first-party Anthropic API rejects one without a top-level
    /// <c>type</c>: <c>400 tools.0.custom.input_schema.type: Field required</c>. A <c>$ref</c>-rooted
    /// schema has no <c>type</c> of its own, so every singular proposer role was unusable on Claude —
    /// invisibly, because Melious accepts the same body and that is where everything ran (live
    /// 2026-08-05, the first request to reach Anthropic in months).</para></summary>
    public static JsonNode BlueprintSliceSchema(string defName)
    {
        var root = JsonNode.Parse(BlueprintSchemaJson)!.AsObject();
        var all = root["$defs"]?.DeepClone().AsObject() ?? new JsonObject();
        if (!all.ContainsKey(defName))
            throw new ArgumentException($"blueprint schema has no $defs/{defName}", nameof(defName));
        var slice = all[defName]!.DeepClone().AsObject();
        slice["$defs"] = ReachableDefs(slice, all);
        return slice;
    }

    /// <summary>A tool schema for a LIST of one blueprint part — what a proposer actually emits
    /// (a set of concepts, a set of fields) rather than a single item.</summary>
    public static JsonNode BlueprintListSchema(string defName, string propertyName)
    {
        var root = JsonNode.Parse(BlueprintSchemaJson)!.AsObject();
        var all = root["$defs"]?.DeepClone().AsObject() ?? new JsonObject();
        if (!all.ContainsKey(defName))
            throw new ArgumentException($"blueprint schema has no $defs/{defName}", nameof(defName));
        var wrapper = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(propertyName),
            ["properties"] = new JsonObject
            {
                [propertyName] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["$ref"] = $"#/$defs/{defName}" },
                },
            },
        };
        wrapper["$defs"] = ReachableDefs(wrapper, all);
        return wrapper;
    }

    /// <summary>A standalone schema for ONE page: <c>#/$defs/page</c> plus the whole <c>$defs</c> table
    /// it references. This is the structural half of validating a personal view — the caller authors a
    /// page, not an app, so the document-level schema (with its required key/name/version/entities) is
    /// the wrong shape to hold them to. Semantics are <see cref="Gate.PageErrors"/>.
    /// <para>The <c>$id</c> is dropped: compiling a derived schema that inherits the App Definition's
    /// registered id throws (see <see cref="Gate.StructuralErrors(JsonNode?, JsonNode)"/>).</para></summary>
    public static JsonNode PageSchema()
    {
        var root = JsonNode.Parse(AppDefinitionSchemaJson)!.AsObject();
        var defs = root["$defs"]?.DeepClone() ?? new JsonObject();
        return new JsonObject { ["$ref"] = "#/$defs/page", ["$defs"] = defs };
    }

    /// <summary>
    /// <see cref="PageSchema"/> with <c>$defs</c> pruned to what the page actually reaches — the
    /// document a CLIENT-SIDE page editor derives its property editors from.
    ///
    /// <para>It is deliberately the same schema the save endpoint validates against, not a
    /// description of it. An editor built from a parallel list of "what a block can have" drifts from
    /// the validator on the first schema change, and the user finds out at save time; built from
    /// this, an editor cannot offer a property the gate would reject.</para>
    ///
    /// <para>Pruning is only about weight — the full table carries <c>entity</c>/<c>field</c>/
    /// <c>workflow</c> and the rest of the document vocabulary, none of which a page can contain.</para>
    /// </summary>
    public static JsonNode PageEditorSchema()
    {
        var schema = PageSchema().AsObject();
        var all = schema["$defs"]!.AsObject();
        schema.Remove("$defs");                      // or the closure walk reaches everything via $defs
        schema["$defs"] = ReachableDefs(schema, all);
        return schema;
    }

    /// <summary>The <c>$defs</c> a schema actually reaches, by transitive closure over <c>$ref</c>.</summary>
    private static JsonObject ReachableDefs(JsonNode root, JsonObject allDefs)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<JsonNode?>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case JsonObject o:
                    if (o["$ref"]?.GetValue<string>() is { } r && r.StartsWith("#/$defs/", StringComparison.Ordinal)
                        && wanted.Add(r["#/$defs/".Length..]) && allDefs[r["#/$defs/".Length..]] is { } def)
                        queue.Enqueue(def);
                    foreach (var kv in o) queue.Enqueue(kv.Value);
                    break;
                case JsonArray a:
                    foreach (var item in a) queue.Enqueue(item);
                    break;
            }
        }
        var kept = new JsonObject();
        foreach (var name in wanted)
            if (allDefs[name] is { } def) kept[name] = def.DeepClone();
        return kept;
    }

    /// <summary>The shape of a saved VIEW PRESET — a personal "Urgent, oldest first, these columns"
    /// over one of the app's existing views. Composed from the definition's own <c>filter</c>,
    /// <c>sort</c> and <c>groupBy</c> defs so a preset's filter means exactly what a view's filter
    /// means; there is no second filter grammar to keep in step. Semantics (do these fields exist on
    /// the base view's entity?) are <see cref="Gate.PresetErrors"/>.</summary>
    public static JsonNode PresetSchema()
    {
        var root = JsonNode.Parse(AppDefinitionSchemaJson)!.AsObject();
        var defs = root["$defs"]?.DeepClone() ?? new JsonObject();
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["filters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 12,
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                },
                ["sort"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 4,
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/sort" },
                },
                ["columns"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 40,
                    ["items"] = new JsonObject { ["$ref"] = "#/$defs/identifier" },
                },
                ["groupBy"] = new JsonObject { ["$ref"] = "#/$defs/groupBy" },
            },
            ["$defs"] = defs,
        };
    }

    /// <summary>The App Definition schema rewritten for the MODEL: the <c>block</c> and
    /// <c>formBlock</c> <c>kind</c> enums carry the namespaced authoring vocabulary
    /// (<see cref="BlockKinds"/>) instead of the canonical flat kinds, and each per-kind
    /// <c>allOf</c> conditional is retargeted at the authoring name so required-property
    /// enforcement survives in the tool schema. Used as the tool input_schema the model targets;
    /// <see cref="Normalizer.LowerAuthoringKinds"/> converts its output back to canonical before
    /// the gate. The stored schema file is never modified — existing definitions keep validating
    /// against the flat vocabulary.</summary>
    /// <param name="context">Optional screen context ("page" | "detail"). When given, block variants
    /// the catalog rules out for that binding are dropped from the union entirely — a page never
    /// sees <c>domain.hub</c>/<c>domain.process</c>/<c>display.fields</c>/<c>domain.child</c>, a
    /// detail never sees <c>domain.view</c>. This is the narrowing the union exists to enable: the
    /// closed variants cost bytes, and the only way to win them back is to stop sending the ones a
    /// slice may not legally use. See <see cref="ComponentCatalog.BlockIsLegalIn"/>.</param>
    public static JsonNode AuthoringSchema(string? context = null)
    {
        var root = JsonNode.Parse(AppDefinitionSchemaJson)!;
        if (root["$defs"] is not JsonObject defs) return root;
        RewriteBlockUnion(defs, context);
        RewriteKinds(defs["formBlock"], ["layout.section", "display.fields"]);
        PublishViewConfigs(defs);
        return root;
    }

    /// <summary>Discriminate <c>view.config</c> by view type, using the config schema the component
    /// catalog already declares for each one.
    /// <para>These rules are not new: <c>Gate.DesignErrors</c> has always validated a view's config
    /// against its catalog entry, and every entry is <c>additionalProperties:false</c>, so
    /// <c>{"banana":true}</c> was already rejected. What was missing is that the model could not SEE
    /// them — the stored schema says <c>config: {"type":"object"}</c>, so the contract only ever
    /// surfaced as a repair round after a failed attempt. This publishes it at authoring time.</para>
    /// <para>The catalog stays the single source of truth: the schema FILE keeps the open
    /// <c>{"type":"object"}</c> rather than a second copy of these rules that could drift from the
    /// one the gate enforces.</para></summary>
    private static void PublishViewConfigs(JsonObject defs)
    {
        if (defs["view"] is not JsonObject view) return;
        if (view["properties"]?["type"]?["enum"] is not JsonArray types) return;

        var allOf = view["allOf"] as JsonArray ?? new JsonArray();
        foreach (var t in types)
        {
            if (t?.GetValue<string>() is not { } type) continue;
            if (ComponentCatalog.Find("view." + type)?.ConfigSchema is not { } config) continue;
            allOf.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = type } },
                    ["required"] = new JsonArray("type"),
                },
                ["then"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["config"] = config.DeepClone() },
                },
            });
        }
        if (allOf.Count > 0) view["allOf"] = allOf;
    }

    /// <summary>Rebuild the block discriminated union in AUTHORING terms. Each canonical variant
    /// (<c>block_row</c>) becomes one variant per authoring name (<c>block_layout_row</c>) whose
    /// <c>kind</c> is the dotted const. Two payoffs beyond the rename:
    /// <list type="bullet">
    /// <item>a canonical kind with NO authoring name (<c>widgets</c>) is dropped outright, so the
    /// model cannot emit it;</item>
    /// <item>a preset-backed name splits into its own variant — <c>control.segmented</c> and
    /// <c>control.stepper</c> become two shapes, the <c>control</c> discriminator property
    /// disappears from the model's view entirely, and the stepper's conditional requirement
    /// (<c>step</c>) becomes a plain <c>required</c> on that one variant.</item>
    /// </list></summary>
    private static void RewriteBlockUnion(JsonObject defs, string? context)
    {
        if (defs["block"] is not JsonObject dispatcher) return;
        var authored = new List<(string DefName, string Kind)>();

        foreach (var k in BlockKinds.All)
        {
            if (context is not null && !ComponentCatalog.BlockIsLegalIn(k.Canonical, context)) continue;
            if (defs[$"block_{k.Canonical}"] is not JsonObject canonical) continue;
            var v = canonical.DeepClone().AsObject();

            if (v["properties"] is JsonObject props)
            {
                props["kind"] = new JsonObject { ["const"] = k.Name };
                if (k.PresetProperty is { } p) props.Remove(p);
            }
            if (k.PresetProperty is { } pp)
            {
                // Fold this preset's conditional requirement in, then drop both the discriminator
                // and the conditional — the NAME now carries what they encoded.
                var extra = PresetRequired(v, k).ToList();
                var kept = new JsonArray();
                foreach (var r in v["required"] as JsonArray ?? [])
                    if (r?.GetValue<string>() is { } s && s != pp) kept.Add(s);
                foreach (var e in extra)
                    if (!kept.Any(x => x!.GetValue<string>() == e)) kept.Add(e);
                v["required"] = kept;
                v.Remove("allOf");
            }

            Contractualize(v);
            var defName = "block_" + k.Name.Replace('.', '_');
            defs[defName] = v;
            authored.Add((defName, k.Name));
        }

        // Drop every canonical variant, including any the model may not author.
        foreach (var name in defs.Select(kv => kv.Key).Where(n => n.StartsWith("block_", StringComparison.Ordinal)).ToList())
            if (!authored.Any(a => a.DefName == name)) defs.Remove(name);

        var kinds = new JsonArray();
        foreach (var (_, kind) in authored) kinds.Add(kind);
        dispatcher["properties"] = new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = kinds },
        };
        var allOf = new JsonArray();
        foreach (var (defName, kind) in authored)
            allOf.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = kind } },
                    ["required"] = new JsonArray("kind"),
                },
                ["then"] = new JsonObject { ["$ref"] = $"#/$defs/{defName}" },
            });
        dispatcher["allOf"] = allOf;
    }

    /// <summary>Cut every description in an authoring variant down to its first sentence.
    /// <para>Closing each variant means its properties are declared 31 times over, so the long
    /// prose that reads well once in the stored schema is paid for once PER VARIANT in the tool
    /// schema — and it duplicates guidance the component catalog already carries in the system
    /// prompt. The tool schema's job is the CONTRACT (types, required, enums); the catalog's job is
    /// when-to-use. The stored schema keeps its full text for humans and external tooling.</para></summary>
    /// <summary>Presentation properties whose meaning is fully carried by their name and enum
    /// (`padding:"sm"`, `weight:"bold"`). They appear on most variants, so their prose is paid for
    /// ~31 times to say nothing the type does not.</summary>
    private static readonly IReadOnlySet<string> SelfEvident = new HashSet<string>(StringComparer.Ordinal)
    {
        "label", "width", "padding", "tone", "bordered", "minHeight", "gap", "align", "wrap",
        "grow", "cols", "size", "weight", "color", "format", "icon", "style", "placeholder",
        "emptyText", "direction", "subordinate", "inlineCreate", "openDetail", "editable",
        "hideWhenEmpty", "hideEmpty",
    };

    /// <summary>
    /// Cuts every property description down to its contract — the first sentence, capped — and drops
    /// the ones the type already says.
    ///
    /// <para><b>RECURSIVE, and it was not.</b> Only a variant's top-level properties were trimmed, so a
    /// property whose value is an OBJECT hid a whole subtree of untouched prose: the calendar's
    /// <c>quickAdd</c> arrived with a 500-character paragraph on one nested field and paid for it on
    /// every screen call, of which one 2026-08-02 run made twenty. Nesting is not a reason for the rule
    /// to stop applying — this schema keeps the contract and the CATALOG carries the teaching, at every
    /// depth.</para>
    /// </summary>
    private static void Contractualize(JsonObject node)
    {
        if (node["properties"] is JsonObject props)
            foreach (var (name, spec) in props)
            {
                if (spec is not JsonObject o) continue;
                if (o["description"]?.GetValue<string>() is { } d)
                {
                    if (SelfEvident.Contains(name)) o.Remove("description");
                    else if (FirstSentence(d) is { } cut && cut.Length < d.Length) o["description"] = cut;
                }
                Contractualize(o);
            }

        // An array's items are a shape like any other, and a list of objects is where the longest
        // descriptions in the catalog live.
        if (node["items"] is JsonObject items) Contractualize(items);
    }

    private static string FirstSentence(string text)
    {
        const int cap = 160;
        var s = text.Replace('\n', ' ').Trim();
        // First sentence end that is not an abbreviation-ish single char before it.
        var stop = s.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0 && stop <= cap) return s[..(stop + 1)];
        if (s.Length <= cap) return s;
        var space = s.LastIndexOf(' ', Math.Min(cap, s.Length - 1));
        return (space > 40 ? s[..space] : s[..cap]).TrimEnd(',', ';', ':', '—', '-', ' ') + "…";
    }

    /// <summary>What the canonical variant's own conditional required for this preset value
    /// (e.g. a 'stepper' control requires `step`).</summary>
    private static IEnumerable<string> PresetRequired(JsonObject variant, BlockKinds.AuthoringKind k)
    {
        if (k.PresetProperty is null || k.PresetValue is null) yield break;
        foreach (var c in variant["allOf"] as JsonArray ?? [])
        {
            if (c?["if"]?["properties"]?[k.PresetProperty]?["const"]?.GetValue<string>() != k.PresetValue) continue;
            foreach (var r in c["then"]?["required"] as JsonArray ?? [])
                if (r?.GetValue<string>() is { } s) yield return s;
        }
    }

    /// <summary>Swap one definition's <c>kind</c> enum + per-kind conditionals over to the
    /// authoring names. Conditionals whose canonical kind has no authoring name (deprecated —
    /// <c>widgets</c>) are dropped as unreachable; preset-backed properties (the <c>control</c>
    /// discriminator, supplied by lowering) are removed so the model neither sets nor needs them.</summary>
    private static void RewriteKinds(JsonNode? def, IReadOnlyList<string> authoringNames)
    {
        if (def is not JsonObject d) return;
        var allowed = new HashSet<string>(authoringNames, StringComparer.Ordinal);

        if (d["properties"]?["kind"] is JsonObject kind)
        {
            var en = new JsonArray();
            foreach (var n in authoringNames) en.Add(n);
            kind["enum"] = en;
            var desc = kind["description"]?.GetValue<string>();
            kind["description"] = (string.IsNullOrWhiteSpace(desc) ? "" : desc + "\n\n")
                + "Namespaced component families:\n" + BlockKinds.FamilyGuide();
        }

        var presetProps = authoringNames
            .Select(BlockKinds.Find)
            .Where(k => k?.PresetProperty is not null)
            .Select(k => k!.PresetProperty!)
            .ToHashSet(StringComparer.Ordinal);
        if (presetProps.Count > 0 && d["properties"] is JsonObject props)
            foreach (var p in presetProps) props.Remove(p);

        if (d["allOf"] is not JsonArray allOf) return;
        var kept = new JsonArray();
        foreach (var entry in allOf.ToList())
        {
            if (entry is not JsonObject e
                || e["if"]?["properties"]?["kind"]?["const"]?.GetValue<string>() is not { } canonical)
            {
                kept.Add(entry!.DeepClone());
                continue;
            }

            var names = BlockKinds.ByCanonical.TryGetValue(canonical, out var ns)
                ? ns.Where(allowed.Contains).ToList()
                : [];
            if (names.Count == 0) continue;   // unreachable for the model (deprecated kind) — drop

            var clone = e.DeepClone().AsObject();
            var kindNode = clone["if"]!["properties"]!["kind"]!.AsObject();
            kindNode.Remove("const");
            if (names.Count == 1)
            {
                kindNode["const"] = names[0];
            }
            else
            {
                var a = new JsonArray();
                foreach (var n in names) a.Add(n);
                kindNode["enum"] = a;
            }

            if (presetProps.Count > 0 && clone["then"]?["required"] is JsonArray req)
            {
                var filtered = new JsonArray();
                foreach (var r in req)
                    if (r?.GetValue<string>() is { } s && !presetProps.Contains(s)) filtered.Add(s);
                if (filtered.Count > 0) clone["then"]!["required"] = filtered;
                else clone["then"]!.AsObject().Remove("required");
            }
            kept.Add(clone);
        }
        d["allOf"] = kept;
    }

    /// <summary>The canonical platform entities (person/department/group) with their fields, as
    /// authored. Apps never declare these — they reference them via <c>targetApp:"platform"</c> and
    /// read them via <c>source.platform</c> — so any tool resolving "what fields does this surface
    /// have" needs them beside the manifest.</summary>
    public static readonly string PlatformEntitiesJson = LoadResource("platform-entities.json");

    /// <summary>Keys of the canonical platform entities (person/department/group).</summary>
    public static readonly IReadOnlySet<string> PlatformEntityKeys = LoadPlatformKeys();

    private static IReadOnlySet<string> LoadPlatformKeys()
    {
        var doc = JsonNode.Parse(PlatformEntitiesJson);
        var keys = new HashSet<string>();
        if (doc?["entities"] is JsonArray entities)
        {
            foreach (var e in entities)
                if (e?["key"]?.GetValue<string>() is { } k)
                    keys.Add(k);
        }
        return keys;
    }
}
