// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// Proves a built app is still the app the user approved.
///
/// <para><b>Why this exists.</b> The runtime compiler deliberately fills gaps: it synthesizes a
/// command for every command-less transition, infers <c>ownedBy</c> and config tags, invents a table
/// view per entity when a definition has none, and composes pages and record details when they are
/// absent. Those are the right behaviours for a definition written by a model that may have left
/// holes. For a blueprint-built app they are the wrong behaviour entirely: every one of them would
/// add something to the running app that nobody agreed to, and the user would never know.</para>
///
/// <para><b>Why by effect rather than by flag.</b> The obvious alternative is a strict switch
/// threaded through each synthesis step. This checks the same guarantee from the outside — compare
/// what was approved against what got built — which is both lower risk (the compiler keeps behaving
/// identically for every existing app) and strictly stronger: a synthesis step added next year is
/// caught by the same comparison, whereas a per-step flag would have to be remembered.</para>
///
/// <para>It is deliberately about STRUCTURE, not decoration. The compiler also assigns option
/// colours, stamps icons, defaults <c>labelPlural</c> and injects the runtime's base fields. Those
/// are documented normalisations that add nothing the user could have opinions about, so they are
/// allowed and named here rather than reported.</para>
/// </summary>
public static class BlueprintConformance
{
    /// <summary>Fields the runtime provides on every entity. Their presence in a manifest is the
    /// compiler doing its job, not inventing a business field.</summary>
    private static readonly HashSet<string> RuntimeFields =
        new(Gate.BaseFields, StringComparer.Ordinal);

    /// <summary>Compares a compiled manifest (or a lowered definition — the shapes overlap where it
    /// matters) against the blueprint it claims to come from. Empty means nothing was invented.</summary>
    public static List<string> Check(Blueprint bp, JsonNode manifest)
    {
        var errors = new List<string>();
        if (manifest is not JsonObject doc)
        {
            errors.Add("CONFORMANCE: the manifest is not an object");
            return errors;
        }

        Entities(bp, doc, errors);
        Commands(bp, doc, errors);
        Views(bp, doc, errors);
        Pages(bp, doc, errors);
        Roles(bp, doc, errors);
        return errors;
    }

    private static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];
    private static string? Str(JsonNode? node, string key) => (node as JsonObject)?[key]?.GetValue<string>();

    private static void Entities(Blueprint bp, JsonObject doc, List<string> e)
    {
        var approved = bp.Concepts.Where(c => ConceptKinds.ProducesEntity(c.Kind))
            .ToDictionary(c => c.Key, c => c, StringComparer.Ordinal);

        foreach (var node in Arr(doc["entities"]))
        {
            if (node is not JsonObject entity || Str(entity, "key") is not { } key) continue;
            if (!approved.TryGetValue(key, out var concept))
            {
                e.Add($"CONFORMANCE: the built app has an entity '{key}' that the blueprint never approved");
                continue;
            }

            // Fields: anything the user did not approve and the runtime did not provide is invented.
            var approvedFields = bp.AllFields.Where(f => f.ConceptId == concept.Id).Select(f => f.Key)
                .Concat(bp.Relationships.Where(r => r.OwningConceptId == concept.Id).Select(r => r.Key))
                .Concat(bp.References.Where(r => r.OnConceptId == concept.Id).Select(r => r.Key))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var fieldNode in Arr(entity["fields"]))
            {
                if (Str(fieldNode, "key") is not { } fieldKey) continue;
                if (RuntimeFields.Contains(fieldKey) || approvedFields.Contains(fieldKey)) continue;
                e.Add($"CONFORMANCE: '{key}' has a field '{fieldKey}' that the blueprint never approved");
            }

            foreach (var missing in approvedFields.Where(f =>
                         Arr(entity["fields"]).All(n => Str(n, "key") != f)))
                e.Add($"CONFORMANCE: '{key}' is missing the approved field '{missing}'");

            // Subordination is a navigation decision: an entity the compiler decided is owned gets
            // no page of its own, which silently removes a destination the user approved.
            var composed = bp.Relationships.Any(r => r.IsComposition && r.ToConceptId == concept.Id);
            var ownedBy = entity["ownedBy"] is JsonObject;
            if (ownedBy && !composed)
                e.Add($"CONFORMANCE: '{key}' was made subordinate at build time, so it lost its own place in the app");
            if (!ownedBy && composed)
                e.Add($"CONFORMANCE: '{key}' was approved as living inside its parent but was built as a standalone list");

            // 'config' moves an entity out of top-level navigation into a Configuration area.
            var builtConfig = Str(entity, "kind") == "config";
            var approvedConfig = concept.Kind == ConceptKinds.Config;
            if (builtConfig != approvedConfig)
                e.Add($"CONFORMANCE: '{key}' was built as "
                    + (builtConfig ? "app configuration" : "an ordinary list")
                    + " but approved as " + (approvedConfig ? "app configuration" : "an ordinary list"));
        }

        foreach (var key in approved.Keys.Where(k => Arr(doc["entities"]).All(n => Str(n, "key") != k)))
            e.Add($"CONFORMANCE: the approved entity '{key}' is missing from the built app");
    }

    /// <summary>The synthesis most likely to bite: the compiler creates a command for every
    /// transition that has none, which puts buttons on records the user never agreed to.</summary>
    private static void Commands(Blueprint bp, JsonObject doc, List<string> e)
    {
        var approved = ApprovedCommandKeys(bp);
        foreach (var node in Arr(doc["commands"]))
        {
            var key = Str(node, "key");
            var entity = Str(node, "entity");
            if (key is null || entity is null) continue;
            if (!approved.Contains((entity, key)))
                e.Add($"CONFORMANCE: the built app has a '{key}' action on '{entity}' that the blueprint never approved");
        }

        foreach (var (entity, key) in approved.Where(a =>
                     Arr(doc["commands"]).All(n => Str(n, "key") != a.Key || Str(n, "entity") != a.Entity)))
            e.Add($"CONFORMANCE: the approved action '{key}' on '{entity}' is missing from the built app");
    }

    /// <summary>
    /// The command keys a conforming build may contain: what lowering produces, plus the one thing
    /// the compiler is allowed to add.
    ///
    /// <para>A transition the user approved but gave no button to (a quote expiring when its
    /// validity date passes) gets a command synthesized as <c>{entity}_{transition}</c>. That is a
    /// documented compiler rule making an approved transition invokable, not an invention — the user
    /// agreed the move exists. What would NOT be allowed is a command for a transition they never
    /// approved, and that is still caught, because the transition would not be here either.</para>
    /// </summary>
    private static HashSet<(string Entity, string Key)> ApprovedCommandKeys(Blueprint bp)
    {
        var keys = new HashSet<(string, string)>();
        foreach (var w in bp.Workflows)
        {
            if (bp.Concept(w.ConceptId) is not { } concept) continue;

            foreach (var a in w.Actions)
            {
                var backed = w.Transitions.Where(t => t.ActionId == a.Id).ToList();
                if (backed.Count == 1) keys.Add((concept.Key, a.Key));
                else foreach (var t in backed) keys.Add((concept.Key, t.Key));
            }

            foreach (var t in w.Transitions.Where(t => t.ActionId is null))
                keys.Add((concept.Key, $"{concept.Key}_{t.Key}"));
        }
        return keys;
    }

    private static void Views(Blueprint bp, JsonObject doc, List<string> e)
    {
        var approved = (bp.Experience?.Surfaces ?? [])
            .Where(s => s.Surface != SurfaceKinds.Detail)
            .Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var node in Arr(doc["views"]))
        {
            if (Str(node, "key") is not { } key) continue;
            if (!approved.Contains(key))
                e.Add($"CONFORMANCE: the built app has a '{key}' view that the blueprint never approved");
        }

        foreach (var key in approved.Where(k => Arr(doc["views"]).All(n => Str(n, "key") != k)))
            e.Add($"CONFORMANCE: the approved view '{key}' is missing from the built app");
    }

    private static void Pages(Blueprint bp, JsonObject doc, List<string> e)
    {
        var approved = (bp.Experience?.Pages ?? []).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var node in Arr(doc["pages"]))
        {
            if (Str(node, "key") is not { } key) continue;
            if (!approved.Contains(key))
                e.Add($"CONFORMANCE: the built app has a '{key}' page that the blueprint never approved");
        }

        foreach (var key in approved.Where(k => Arr(doc["pages"]).All(n => Str(n, "key") != k)))
            e.Add($"CONFORMANCE: the approved page '{key}' is missing from the built app");
    }

    /// <summary>Roles are the one place where a missing item is a security problem rather than a
    /// usability one: an actor whose role vanished does not lose a screen, it loses its limits.</summary>
    private static void Roles(Blueprint bp, JsonObject doc, List<string> e)
    {
        var approved = bp.Actors.ToDictionary(a => a.Key, a => a, StringComparer.Ordinal);

        foreach (var node in Arr(doc["roles"]))
        {
            if (Str(node, "key") is not { } key) continue;
            if (!approved.ContainsKey(key))
                e.Add($"CONFORMANCE: the built app has a '{key}' role that the blueprint never approved");
        }

        foreach (var key in approved.Keys.Where(k => Arr(doc["roles"]).All(n => Str(n, "key") != k)))
            e.Add($"CONFORMANCE: the approved role '{key}' is missing from the built app");

        // A field the user was promised would be hidden must still be hidden after the build.
        foreach (var actor in bp.Actors)
        {
            var role = Arr(doc["roles"]).FirstOrDefault(n => Str(n, "key") == actor.Key) as JsonObject;
            if (role is null) continue;

            foreach (var fp in actor.FieldPermissions.Where(p => !p.Visible || !p.Editable))
            {
                if (bp.Field(fp.FieldId) is not { } field) continue;
                if (bp.Concept(field.ConceptId) is not { } concept) continue;

                var grant = Arr(role["grants"]).FirstOrDefault(g => Str(g, "entity") == concept.Key) as JsonObject;
                var over = grant is null
                    ? null
                    : Arr(grant["fieldOverrides"]).FirstOrDefault(o => Str(o, "field") == field.Key) as JsonObject;

                if (over is null)
                {
                    e.Add($"CONFORMANCE: '{actor.Name}' was approved as unable to see or edit '{field.Label}', "
                        + "but the built app puts no restriction on it");
                    continue;
                }
                if (!fp.Visible && over["read"]?.GetValue<bool>() != false)
                    e.Add($"CONFORMANCE: '{actor.Name}' can read '{field.Label}' in the built app, "
                        + "which the blueprint says they must not");
                if (!fp.Editable && over["update"]?.GetValue<bool>() != false)
                    e.Add($"CONFORMANCE: '{actor.Name}' can edit '{field.Label}' in the built app, "
                        + "which the blueprint says they must not");
            }
        }
    }
}
