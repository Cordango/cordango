// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// The human-readable text inside an App Definition or a compiled manifest, addressed by KEY PATH.
///
/// <para>This is the foundation of app localization (see <c>_docs/10-platform/plan-localization.md</c>).
/// The definition stays single-language. A translation bundle (<see cref="AppTextBundle"/>) sits
/// beside it and is applied to the COMPILED MANIFEST by <see cref="Translate"/>, not to the
/// definition before compiling: the compiler adds English of its own (system fields, the record-state
/// options, "label + s" plurals, "label?" confirmations) and guesses icons and option colours from
/// labels, so translating first would leave its English untranslated and could change icons.</para>
///
/// <para>Why a sidecar rather than inline <c>{ "en": …, "de": … }</c> values: every label in the
/// schema would become a union of string-or-object, and the Gate, the standalone editor, the shared
/// blockspec and the generator's tool schema would all have to learn both shapes. A bundle changes
/// none of them, and an untranslated app is a bundle with fewer entries rather than a definition in a
/// broken state.</para>
/// </summary>
public static class DefinitionText
{
    /// <summary>
    /// The property names that hold text a HUMAN reads, anywhere in the document.
    ///
    /// <para>Deliberately a name list rather than a set of schema pointers. The schema has ~56
    /// distinct <c>label</c> declarations across block kinds, and enumerating their pointers would
    /// mean this file had to be edited every time a block gains one. The names below are reserved
    /// for display text throughout the schema — a machine value is a <c>key</c>, an <c>entity</c>,
    /// a <c>field</c> or a <c>value</c>, never a <c>label</c>.</para>
    /// </summary>
    private static readonly HashSet<string> TextProperties = new(StringComparer.Ordinal)
    {
        "label", "labelPlural", "title", "subtitle", "description", "placeholder", "hint",
        "emptyText", "empty", "successMessage", "helpText", "caption", "text",
        "help", "message", "confirmLabel", "tagline", "summary", "headline",
    };

    /// <summary>
    /// Property names that look like text and are NOT.
    ///
    /// <para><c>name</c> and <c>group</c> are the traps: each is display text in one place and an
    /// identifier in others. They are handled by path in <see cref="IsText"/>, and everything here is
    /// excluded outright.</para>
    /// </summary>
    private static readonly HashSet<string> NeverText = new(StringComparer.Ordinal)
    {
        // A select option's `value` is the STORED code; its `label` beside it is the display text.
        // Translating the value would rewrite the data.
        "value", "key", "entity", "field", "targetEntity", "targetApp", "icon", "color", "type",
        "kind", "format", "expr", "role", "roleKey", "handle", "slug", "state", "stateField",
    };

    /// <summary>Block kinds that put a FIELD KEY in a text-named property: a hub's <c>title</c> names
    /// the field its heading is read from, an org chart's <c>subtitle</c> the field under each name.</summary>
    private static readonly HashSet<(string Kind, string Property)> FieldReferences =
    [
        ("hub", "title"),
        ("orgchart", "subtitle"),
    ];

    /// <summary>Block kinds whose <c>value</c> is the words drawn rather than a stored code: a text
    /// block's sentence (with <c>{field}</c> holes the renderer fills) and an unbound chip's label.</summary>
    private static readonly HashSet<string> TextValueKinds = new(StringComparer.Ordinal) { "text", "chip" };

    /// <summary>Objects that map FIELD KEYS to the values an effect or a form writes into a record:
    /// <c>set: { title: "{{record.title}}" }</c> is data, whatever its property is called.</summary>
    private static readonly HashSet<string> RecordValueMaps = new(StringComparer.Ordinal)
    {
        "set", "setIfEmpty", "defaults", "prefill", "values",
    };

    /// <summary>Root sections written for builders and the compiler, never drawn for a user.</summary>
    private static readonly HashSet<string> SkipRoot = new(StringComparer.Ordinal)
    {
        "purpose", "uses", "relations", "plugins", "theme", "storage", "archetype",
    };

    /// <summary>One translatable string: where it lives, and what it currently says.</summary>
    /// <param name="Path">A slash path from the document root, e.g.
    /// <c>entities/deal/fields/stage/options/won/label</c>. An array element is addressed by its
    /// <c>key</c>, else its <c>value</c>, else its index — so a path survives reordering and an
    /// insertion elsewhere, and a renamed key makes the path a MISS, which falls back to the text
    /// lookup instead of landing on a neighbour.</param>
    public readonly record struct Entry(string Path, string Text);

    /// <summary>
    /// Every human-readable string in a document, in document order.
    ///
    /// <para>Used to build the translator's worklist, to detect what a definition change invalidated,
    /// and to assert in tests that a bundle covers a shipped app.</para>
    /// </summary>
    public static IReadOnlyList<Entry> Extract(JsonNode? document)
    {
        var entries = new List<Entry>();
        if (document is JsonObject root) Walk(root, "", false, (_, _, path, text) => entries.Add(new Entry(path, text)));
        return entries;
    }

    private delegate void Visitor(JsonObject parent, string property, string path, string text);

    private static void Walk(JsonObject obj, string path, bool inOptions, Visitor visit)
    {
        foreach (var (name, child) in obj)
        {
            if (child is null || (path.Length == 0 && SkipRoot.Contains(name))) continue;
            var childPath = path.Length == 0 ? Escape(name) : $"{path}/{Escape(name)}";
            switch (child)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    if (!string.IsNullOrWhiteSpace(text) && IsText(name, path, obj, inOptions))
                        visit(obj, name, childPath, text);
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        WalkElement(array[i], $"{childPath}/{Segment(array[i], i)}", name == "options", visit);
                    break;
                case JsonObject nested when !RecordValueMaps.Contains(name):
                    Walk(nested, childPath, false, visit);
                    break;
            }
        }
    }

    // A plain string inside an array is never text: `options: ["Low", "High"]` STORES those strings.
    private static void WalkElement(JsonNode? element, string path, bool isOption, Visitor visit)
    {
        if (element is JsonObject obj) Walk(obj, path, isOption, visit);
        else if (element is JsonArray nested)
            for (var i = 0; i < nested.Count; i++)
                WalkElement(nested[i], $"{path}/{Segment(nested[i], i)}", false, visit);
    }

    private static string Segment(JsonNode? element, int index) =>
        element is JsonObject o
            ? Escape(NonEmpty(o, "key") ?? NonEmpty(o, "value") ?? index.ToString())
            : index.ToString();

    // JSON Pointer escaping, so an option value like "n/a" stays one segment.
    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static string? NonEmpty(JsonObject o, string property) =>
        o[property] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static bool IsText(string property, string parentPath, JsonObject parent, bool inOption)
    {
        var kind = NonEmpty(parent, "kind");
        if (property == "value" && kind is not null && TextValueKinds.Contains(kind)) return true;
        if (NeverText.Contains(property)) return false;
        if (kind is not null && FieldReferences.Contains((kind, property))) return false;
        // An entity's `calendar` names the FIELD its events are titled from.
        if (property == "title" && parentPath.Split('/') is [_, _, "calendar"]) return false;
        if (TextProperties.Contains(property))
            // An option without a `value` stores its LABEL, so translating the label rewrites data.
            return property != "label" || !inOption || NonEmpty(parent, "value") is not null;
        return property switch
        {
            // The app's own display name, and a role's. Deeper `name`s are identifiers.
            "name" => parentPath.Length == 0 || (parentPath.StartsWith("roles/", StringComparison.Ordinal)
                                                 && parentPath.IndexOf('/', 6) < 0),
            // A field's section heading. A page's `group` ('config') is machine.
            "group" => parentPath.Split('/') is { Length: >= 2 } segments && segments[^2] == "fields",
            _ => false,
        };
    }

    /// <summary>
    /// Translate every display string in a document, returning a NEW document and what no layer
    /// covered.
    ///
    /// <para>For each string, in order: a layer's <see cref="AppTextBundle.At"/> entry for its exact
    /// path (the homonym override), then each layer's <see cref="AppTextBundle.Text"/> entry for its
    /// English text, most specific layer first. A confirmation the compiler made by appending "?" to
    /// a command label is found through the label. Anything left keeps its authoring language, so a
    /// half-translated app is a mix rather than a page of blanks — a missing German label is still
    /// readable and an empty one is not.</para>
    ///
    /// <para>Only string values are ever replaced, and never one <see cref="IsText"/> rejects — a
    /// stored option value, a key or a page's machine group cannot be reached from a bundle.</para>
    /// </summary>
    public static (JsonNode Document, IReadOnlyList<Entry> Untranslated) Translate(
        JsonNode document, IReadOnlyList<AppTextBundle> layers)
    {
        ArgumentNullException.ThrowIfNull(document);
        var doc = document.DeepClone();
        var untranslated = new List<Entry>();
        if (doc is not JsonObject root) return (doc, untranslated);

        // Collected first, written after: a JsonObject cannot be assigned to while it is enumerated.
        var writes = new List<(JsonObject Parent, string Property, string Text)>();
        Walk(root, "", false, (parent, property, path, text) =>
        {
            if (Lookup(path, text, layers) is { } translated) writes.Add((parent, property, translated));
            else if (HasWords(text)) untranslated.Add(new Entry(path, text));
        });
        foreach (var (parent, property, text) in writes) parent[property] = JsonValue.Create(text);
        return (doc, untranslated);
    }

    /// <summary>Whether a string says anything outside its placeholders. <c>{{column.label}}</c> alone
    /// is the same in every language, so it is never reported as untranslated.</summary>
    private static bool HasWords(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && char.IsLetter(c)) return true;
        }
        return false;
    }

    /// <summary>One string's translation by the same rules as <see cref="Translate"/>, or null. For
    /// text shown outside a manifest — an app's name in a launcher — pass the path it has there.</summary>
    public static string? Lookup(string? path, string text, IReadOnlyList<AppTextBundle> layers)
    {
        if (path is not null)
            foreach (var layer in layers)
                if (layer.At.TryGetValue(path, out var at) && !string.IsNullOrWhiteSpace(at)) return at;
        foreach (var layer in layers)
            if (layer.Text.TryGetValue(text, out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        if (text.Length > 1 && text[^1] == '?')
            foreach (var layer in layers)
                if (layer.Text.TryGetValue(text[..^1], out var stem) && !string.IsNullOrWhiteSpace(stem))
                    return stem + "?";
        return null;
    }

    /// <summary>
    /// Apply a path-keyed bundle to a document, returning a NEW document.
    ///
    /// <para>Paths in the bundle that no longer resolve are skipped rather than created: the document
    /// is the shape, and a stale bundle must not be able to invent structure in it.</para>
    /// </summary>
    /// <returns>The localized document, and how many entries were applied vs skipped.</returns>
    public static (JsonNode Document, int Applied, int Skipped) Apply(
        JsonNode definition, IReadOnlyDictionary<string, string> bundle)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var doc = definition.DeepClone();
        int applied = 0, skipped = 0;

        foreach (var (path, text) in bundle)
        {
            if (string.IsNullOrWhiteSpace(text)) { skipped++; continue; }
            if (Resolve(doc, path) is { } slot && TrySet(slot.Parent, slot.Name, slot.Index, text)) applied++;
            else skipped++;
        }
        return (doc, applied, skipped);
    }

    private readonly record struct Slot(JsonNode Parent, string? Name, int Index);

    /// <summary>Walk a key path to the node that HOLDS the value, so it can be replaced in place.</summary>
    private static Slot? Resolve(JsonNode root, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Step(current, segments[i]);
            if (current is null) return null;
        }

        var last = segments[^1];
        return current switch
        {
            JsonArray arr when IndexOf(arr, last) is var index and >= 0 => new Slot(current, null, index),
            JsonObject obj when Unescape(last) is var name && obj.ContainsKey(name) => new Slot(current, name, -1),
            _ => null,
        };
    }

    private static JsonNode? Step(JsonNode node, string segment) => node switch
    {
        JsonArray arr => IndexOf(arr, segment) is var index and >= 0 ? arr[index] : null,
        JsonObject obj => obj.TryGetPropertyValue(Unescape(segment), out var child) ? child : null,
        _ => null,
    };

    private static string Unescape(string segment) => segment.Replace("~1", "/").Replace("~0", "~");

    private static int IndexOf(JsonArray array, string segment)
    {
        for (var i = 0; i < array.Count; i++)
            if (Segment(array[i], i) == segment) return i;
        return -1;
    }

    private static bool TrySet(JsonNode parent, string? name, int index, string text)
    {
        switch (parent)
        {
            case JsonObject obj when name is not null:
                // Only ever REPLACE a string. A bundle that points at an object or an array is
                // pointing at structure, and overwriting structure with a sentence is how a
                // definition becomes unloadable.
                if (obj[name] is not JsonValue existing || !existing.TryGetValue<string>(out _)) return false;
                obj[name] = JsonValue.Create(text);
                return true;
            case JsonArray arr when index >= 0:
                if (arr[index] is not JsonValue item || !item.TryGetValue<string>(out _)) return false;
                arr[index] = JsonValue.Create(text);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// What a path-keyed bundle no longer covers, and what it covers that no longer exists.
    ///
    /// <para>The maintenance question, answered without a model: after a definition is edited, which
    /// strings are new (need translating), and which bundle entries are pointing at text that has
    /// since changed underneath them (need re-translating)? Both are far more common than a full
    /// re-translation, and both are cheap to compute.</para>
    /// </summary>
    /// <param name="sourceHashes">The bundle's record of what each path SAID when it was translated.
    /// A path whose text has moved on is stale.</param>
    public static (IReadOnlyList<string> Missing, IReadOnlyList<string> Stale, IReadOnlyList<string> Orphaned)
        Diff(JsonNode definition, IReadOnlyDictionary<string, string> bundle,
             IReadOnlyDictionary<string, string>? sourceHashes = null)
    {
        var entries = Extract(definition);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries) paths.TryAdd(e.Path, e.Text);

        var missing = entries.Where(e => !bundle.ContainsKey(e.Path)).Select(e => e.Path).ToList();
        var orphaned = bundle.Keys.Where(p => !paths.ContainsKey(p)).ToList();
        var stale = sourceHashes is null
            ? []
            : bundle.Keys
                .Where(p => paths.TryGetValue(p, out var now)
                            && sourceHashes.TryGetValue(p, out var then)
                            && !string.Equals(now, then, StringComparison.Ordinal))
                .ToList();

        return (missing, stale, orphaned);
    }
}
