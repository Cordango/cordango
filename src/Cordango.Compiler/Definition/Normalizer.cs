// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// Deterministic pre-gate repair of the classic weak-model tool-call defects. Two of them:
/// (1) emitting a JSON object/array as its STRING encoding ("presentation": "{\"icon\": ...}") —
/// walked against the App Definition schema, wherever the schema expects an object/array but the
/// value is a string that parses to that shape it is replaced in place; (2) wrapping the whole
/// definition under a single envelope key ({"app": {…}}) instead of emitting it at the root.
/// Anything else is left untouched, so legitimate string fields are never mangled. Fixing these
/// here costs nothing; fixing them via the repair loop costs a full model pass.
/// </summary>
public static class Normalizer
{
    /// <summary>Envelope keys a model sometimes wraps the whole definition under instead of
    /// emitting it at the root (e.g. {"app": {…}}). Unwrapped when the root is missing the real
    /// required keys and the single wrapper value looks like a definition.</summary>
    // "input": deepseek-v4-pro wrapped the whole definition as {"input": {…}} (2026-07-19 run) —
    // the model echoing the tool-call convention's own argument-envelope name.
    private static readonly HashSet<string> WrapperKeys = new(StringComparer.OrdinalIgnoreCase)
        { "app", "appDefinition", "app_definition", "definition", "result", "output", "input" };

    /// <summary>Parse MODEL-AUTHORED JSON tolerantly: duplicate property names resolve LAST-WINS
    /// (JavaScript `JSON.parse` semantics) instead of crashing later. `JsonNode.Parse` accepts a
    /// duplicate lazily but materializing the object throws "An item with the same key has
    /// already been added" — observed live 2026-07-19: a generated definition carried two "key"
    /// properties in one object and the unhandled exception killed the whole run before the
    /// repair loop ever saw the document. JsonDocument tolerates duplicates, so parse to elements
    /// and rebuild nodes with last-wins assignment.</summary>
    public static JsonNode? ParseTolerant(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        return FromElement(doc.RootElement);
    }

    private static JsonNode? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = FromElement(p.Value);   // last wins
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var item in e.EnumerateArray()) a.Add(FromElement(item));
                return a;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(e.Clone());   // detach the scalar from the parent document
        }
    }

    /// <summary>One double-encoded section that parsed only after a trailing suffix was tolerated.
    /// Collected so lenient recovery can be MEASURED: it exists to save a document the model already
    /// wrote correctly, and if a provider's corruption rate climbs, that has to be visible rather
    /// than silently absorbed.</summary>
    /// <param name="Path">JSON-pointer-ish location of the recovered value, e.g. "/page".</param>
    /// <param name="Expected">"object" or "array" — what the schema wanted there.</param>
    /// <param name="ParsedLength">Bytes consumed by the recovered value.</param>
    /// <param name="Suffix">The tolerated remainder, verbatim.</param>
    public sealed record LenientRecovery(string Path, string Expected, int ParsedLength, string Suffix);

    /// <summary>Non-whitespace characters tolerated after a recovered value. Excess closing
    /// delimiters come in ones and twos; a long run of them is a corrupted document rather than a
    /// model closing one bracket too many.</summary>
    private const int MaxSuffixChars = 8;

    /// <summary>Un-stringify double-encoded objects/arrays against the App Definition schema.
    /// Returns the same (mutated) node, or the replacement if the ROOT itself was a string.</summary>
    public static JsonNode? Unstringify(JsonNode? doc, ICollection<LenientRecovery>? recoveries = null)
    {
        doc = Unwrap(doc);
        var schema = Schemas.AppDefinitionSchemaNode();
        return Walk(doc, schema, schema, "", recoveries);
    }

    /// <summary>Peel a single envelope wrapper off the root when the model nested the definition
    /// under {"app": {…}} (etc.) instead of emitting it directly. Conservative: only fires when the
    /// root itself lacks the required top-level keys AND has exactly one property, a wrapper-named
    /// object that itself looks like a definition. Never touches a valid document.</summary>
    private static JsonNode? Unwrap(JsonNode? doc)
    {
        if (doc is not JsonObject root) return doc;
        if (root.ContainsKey("schemaVersion") || root.ContainsKey("key") || root.ContainsKey("entities"))
            return doc; // already a real definition at the root
        if (root.Count != 1) return doc;
        var (name, value) = (root.First().Key, root.First().Value);
        if (!WrapperKeys.Contains(name) || value is not JsonObject inner) return doc;
        if (!inner.ContainsKey("entities") && !inner.ContainsKey("schemaVersion")) return doc;
        return inner.DeepClone(); // detach from the wrapper before further walking
    }

    /// <summary>Same repair against an arbitrary schema (e.g. the design-pass tool schema).
    /// Local "#/..." $refs are resolved within <paramref name="schema"/>.</summary>
    public static JsonNode? Unstringify(JsonNode? doc, JsonNode schema,
        ICollection<LenientRecovery>? recoveries = null) => Walk(doc, schema, schema, "", recoveries);

    /// <summary>FULL pre-gate repair of a model-authored document: <see cref="Unstringify"/> then
    /// <see cref="LowerAuthoringKinds"/>. Use this at every point where model output heads for the
    /// gate — never bare <c>Unstringify</c>, or namespaced kinds reach the gate as errors.
    /// <para><b>Order matters:</b> un-stringifying is schema-aware and the model's tool schema is
    /// the AUTHORING one, so it must run while the document still speaks the dotted vocabulary;
    /// lowering comes after.</para>
    /// <para><b>Not</b> used by the provider's tool-argument validation
    /// (<c>Providers.ToolArgumentSchemaErrors</c>): that checks the model's raw argument against
    /// the dotted tool schema, so it must NOT be lowered first.</para></summary>
    public static JsonNode? Repair(JsonNode? doc, ICollection<LenientRecovery>? recoveries = null) =>
        LowerAuthoringKinds(Unstringify(doc, recoveries));

    /// <inheritdoc cref="Repair(JsonNode?, ICollection{LenientRecovery})"/>
    public static JsonNode? Repair(JsonNode? doc, JsonNode schema,
        ICollection<LenientRecovery>? recoveries = null) =>
        LowerAuthoringKinds(Unstringify(doc, schema, recoveries));

    /// <summary>Lower the namespaced AUTHORING vocabulary the model writes (<c>layout.row</c>,
    /// <c>data.repeat</c>, <c>control.stepper</c>) back to the canonical flat block kinds the
    /// schema, gate, compiler and renderer speak — see <see cref="BlockKinds"/>. A name carrying a
    /// preset also gets its discriminator set (<c>control.stepper</c> →
    /// <c>{kind:"control", control:"stepper"}</c>).
    /// <para>Walks every object with a string <c>kind</c>. Values that are not authoring names are
    /// left ALONE: a canonical kind the model emitted from memory still validates, and a genuinely
    /// bad one must reach the gate as an error rather than be silently swallowed. Other uses of the
    /// key <c>kind</c> (entity <c>collection|config|settings</c>, <c>archetype.kind</c>) are
    /// untouched because no authoring name collides with them.</para></summary>
    public static JsonNode? LowerAuthoringKinds(JsonNode? doc)
    {
        LowerWalk(doc);
        return doc;
    }

    private static void LowerWalk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["kind"] is JsonValue kv
                    && kv.TryGetValue<string>(out var name)
                    && BlockKinds.Find(name) is { } k)
                {
                    o["kind"] = k.Canonical;
                    if (k.PresetProperty is { } p && k.PresetValue is { } v) o[p] = v;
                }
                foreach (var child in o.ToList()) LowerWalk(child.Value);
                break;
            case JsonArray a:
                foreach (var item in a.ToList()) LowerWalk(item);
                break;
        }
    }

    private static JsonNode? Walk(JsonNode? node, JsonNode? schema, JsonNode root,
        string path, ICollection<LenientRecovery>? recoveries)
    {
        schema = Resolve(schema, root);
        if (node is null || schema is not JsonObject s) return node;

        // `type` may be a UNION — `"type": ["boolean", "object"]`, which is how the entity `calendar`
        // opt-in says it is either a bare flag or a configured object. Read as a SET rather than as a
        // string: GetValue<string> throws on the array form, and it threw from HERE, so the whole
        // document walk died on the first entity carrying that key. It surfaced as an
        // InvalidOperationException out of `check`, `build` and every Generator repair pass, with
        // nothing in it naming a schema node that states two types — so it read as "the definition is
        // broken" rather than "the walk cannot read this schema".
        var types = Types(s["type"]);
        var wantsObject = types.Contains("object") || s["properties"] is JsonObject;
        var wantsArray = types.Contains("array") || (types.Count == 0 && s["items"] is not null);

        if (node is JsonValue v && v.TryGetValue<string>(out string? str))
        {
            // STRUCTURED ONLY: the schema must want an object/array here AND the string must open
            // one. Walk is generic, so without this an ordinary text field, id or piece of user
            // content could be silently reinterpreted as JSON.
            var trimmed = str.TrimStart();
            if (!(wantsObject && trimmed.StartsWith('{')) && !(wantsArray && trimmed.StartsWith('[')))
                return node;
            var parsed = ParseLenient(str, out var suffix, out var parsedLength);
            if (parsed is not (JsonObject or JsonArray)) return node;
            if (suffix.Length > 0)
                recoveries?.Add(new LenientRecovery(path, wantsObject ? "object" : "array",
                    parsedLength, suffix));
            node = parsed;
        }

        if (node is JsonObject obj && s["properties"] is JsonObject props)
        {
            foreach (var name in obj.Select(kv => kv.Key).ToList())
            {
                if (props[name] is not { } sub) continue;
                var child = obj[name];
                var replaced = Walk(child, sub, root, $"{path}/{name}", recoveries);
                if (!ReferenceEquals(replaced, child)) obj[name] = replaced;
            }
        }
        else if (node is JsonArray arr && s["items"] is { } items)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var child = arr[i];
                var replaced = Walk(child, items, root, $"{path}/{i}", recoveries);
                if (!ReferenceEquals(replaced, child)) arr[i] = replaced;
            }
        }
        return node;
    }

    /// <summary>
    /// A schema's <c>type</c> as a set: absent, one name, or a union of several.
    ///
    /// <para>JSON Schema allows all three and this walk meets all three, so reading it as a string is
    /// a crash waiting for the first union anybody adds to the schema. A non-string member is
    /// dropped rather than refused — this is a repair pass, and it has no business being the thing
    /// that rejects a malformed schema.</para>
    /// </summary>
    private static List<string> Types(JsonNode? type) => type switch
    {
        JsonValue v when v.TryGetValue<string>(out var one) => [one],
        JsonArray a => [.. a.OfType<JsonValue>()
            .Select(x => x.TryGetValue<string>(out var t) ? t : null)
            .OfType<string>()],
        _ => [],
    };

    /// <summary>Parse a double-encoded section that may carry TRAILING GARBAGE. Strict parse first,
    /// so a well-formed document takes exactly the path it always did; only a strict failure reaches
    /// the tolerant one.
    /// <para>The tolerant path reads ONE value and accepts the remainder only when it is pure
    /// closing punctuation — whitespace, ']' and '}'. This is not hypothetical: on 2026-08-02 the
    /// design critic's repair returned a 6320-character `page` whose last two characters were a
    /// stray "]}". The strict parse threw, the slice was discarded, the repair was abandoned, and
    /// the run still reported designOk.</para>
    /// <para>A remainder carrying CONTENT is REFUSED, and so is one led by a COMMA. Excess '}' is a
    /// model closing one bracket too many; a comma says another value was intended and lost, which
    /// makes the prefix an INCOMPLETE document rather than a complete one with garbage after it.
    /// The same run proves the distinction: its first attempt's `page` parsed cleanly at offset 661
    /// with ", {\"kind\": \"data.tiles\", …" and 8,267 further characters of real blocks after it.
    /// Keeping that prefix would have replaced a whole page with a three-block stub — a downgrade
    /// that looks like a success. Recovering the two-character case is free; "recovering" the other
    /// one is not.</para></summary>
    private static JsonNode? ParseLenient(string str, out string suffix, out int parsedLength)
    {
        suffix = "";
        parsedLength = 0;
        try { return JsonNode.Parse(str); }
        catch (JsonException) { /* fall through to the tolerant path */ }

        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(str);
            var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
            if (!JsonDocument.TryParseValue(ref reader, out var doc)) return null;
            using (doc)
            {
                var consumed = (int)reader.BytesConsumed;
                var rest = System.Text.Encoding.UTF8.GetString(bytes, consumed, bytes.Length - consumed);
                var significant = rest.Count(c => !char.IsWhiteSpace(c));
                if (significant == 0) return null;                     // strict parse would have won
                if (significant > MaxSuffixChars) return null;
                if (rest.Any(c => c is not (']' or '}') && !char.IsWhiteSpace(c))) return null;
                suffix = rest;
                parsedLength = consumed;
                return FromElement(doc.RootElement);                   // last-wins on duplicate keys
            }
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Follow a local "#/..." $ref within the schema document.</summary>
    private static JsonNode? Resolve(JsonNode? schema, JsonNode root)
    {
        if (schema?["$ref"]?.GetValue<string>() is not { } r || !r.StartsWith("#/")) return schema;
        JsonNode? cur = root;
        foreach (var seg in r[2..].Split('/'))
            cur = cur?[seg.Replace("~1", "/").Replace("~0", "~")];
        return cur;
    }
}
