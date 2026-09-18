// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The one failure the link cannot detect in itself: a property that names an entity and that
/// nobody told it about.
///
/// <para><b>Why this is a test rather than a careful list.</b> The rewrite is driven by an explicit
/// set of property names, because rewriting by VALUE corrupts the many places that hold a field key
/// which happens to equal an entity key. An explicit set is only as good as the day it was written —
/// <c>whoEntity</c> is emitted by the calendar resolver and is on no list anybody would think to
/// make, and a link that missed it would put records in nobody's calendar in silence.</para>
///
/// <para>So this walks every application in the corpus and asserts that every property whose value
/// IS an entity key is one of two things: a property the link rewrites, or a property known to hold
/// a FIELD key and merely collide with an entity name. A new one in either category fails here,
/// while somebody is adding it, rather than in a generated application months later.</para>
/// </summary>
public class WorkspaceLinkCoverageTests
{
    /// <summary>
    /// Properties that hold a FIELD key (or a label, or a state) and are expected to collide with an
    /// entity key from time to time. Every one of these was observed doing exactly that in the
    /// corpus: an entity `portfolio` and a field `portfolio` pointing at it is the ordinary way to
    /// name a reference, not a coincidence worth avoiding.
    /// </summary>
    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal)
    {
        "via", "groupBy", "displayField", "who", "field", "orderField", "imageField",
        "imageNameField", "colorField", "dateField", "endField", "startField", "match",
        "titleField", "statusField", "start", "end", "key", "value", "label", "name", "state",
        "parentField", "path", "target", "source", "at", "from", "to", "against", "mapsTo",

        // `inverseField` is the field on the other end of a relation; `as` is a repeat block's scope
        // alias and names no key at all; `title` is a heading. All three were caught by this test
        // looking like entity keys, and all three are correctly left alone.
        "inverseField", "as", "title",
    };

    [Fact]
    public void Every_property_that_names_an_entity_is_one_the_link_knows_about()
    {
        var known = Rewritten();
        var missed = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, manifest) in Corpus())
        {
            var entities = manifest["entities"]?.AsArray()
                .OfType<JsonObject>()
                .Select(e => (string?)e["key"])
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal) ?? [];

            Walk(manifest, (property, value) =>
            {
                if (!entities.Contains(value)) return;
                if (known.Contains(property) || FieldKeys.Contains(property)) return;
                missed.TryAdd(property, $"{name}: '{property}' = '{value}'");
            });
        }

        Assert.True(missed.Count == 0,
            "These properties hold what looks like an ENTITY key and the link neither rewrites them "
            + "nor knows them to be field keys. If they name an entity, add them to "
            + "WorkspaceLink.EntityRefs or a renamed entity will leave them dangling. If they hold a "
            + "field key, add them to FieldKeys here:\n  "
            + string.Join("\n  ", missed.Values));
    }

    /// <summary>The link's own list, read off it rather than copied — a copy would agree with itself
    /// for ever.</summary>
    private static HashSet<string> Rewritten()
    {
        var field = typeof(WorkspaceLink).GetField("EntityRefs",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return [.. (string[])field.GetValue(null)!];
    }

    private static IEnumerable<(string Name, JsonObject Manifest)> Corpus()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference");
        foreach (var path in System.IO.Directory.EnumerateFiles(root, "*.appdef.json").OrderBy(p => p, StringComparer.Ordinal))
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject manifest)
                yield return (Path.GetFileNameWithoutExtension(path), manifest);
    }

    private static void Walk(JsonNode? node, Action<string, string> visit)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (property, child) in o)
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var text)) visit(property, text);
                    Walk(child, visit);
                }

                return;

            case JsonArray a:
                foreach (var child in a) Walk(child, visit);
                return;
        }
    }
}
