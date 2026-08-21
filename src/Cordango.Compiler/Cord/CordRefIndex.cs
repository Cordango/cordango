// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// Which local reference fields each entity has, and what they point at — the one fact
/// <c>rollup.via</c> can be derived from.
///
/// <para><b>Why this exists.</b> <c>via</c> is not a decision an author makes; it is a lookup they are
/// asked to perform. "The reference field on the aggregated entity that points at the record both of
/// these share" is a sentence about the schema, not about the business, and getting it wrong is one of
/// the gate errors the design agent actually hits. Measured over the corpus, the answer is unique in
/// <b>45 of 45</b> aggregates — so it is not a choice at all, and the author should never be asked.</para>
///
/// <para><b>Why one class serves both directions.</b> Import must decide whether inference would
/// reproduce the <c>via</c> a document already contains, and lowering must produce it. If those used
/// two implementations they would disagree eventually, and the symptom would be a round-trip that
/// fails on one app for reasons nobody can see. Both build the same index — one from a definition, one
/// from the model — and then call the same <see cref="Infer"/>.</para>
/// </summary>
public sealed class CordRefIndex
{
    /// <summary>entity key → (reference field key → the entity it targets). LOCAL references only:
    /// a field carrying <c>targetApp</c> points into another app and can never be the join between two
    /// records of this one.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _byEntity = new(StringComparer.Ordinal);

    public static CordRefIndex FromDefinition(JsonNode? definition)
    {
        var index = new CordRefIndex();
        foreach (var entity in (definition as JsonObject)?["entities"] as JsonArray ?? [])
        {
            if (entity is not JsonObject e || Str(e, "key") is not { } key) continue;
            var refs = index.Slot(key);
            foreach (var field in e["fields"] as JsonArray ?? [])
            {
                if (field is not JsonObject f) continue;
                if (Str(f, "type") != "reference" || Str(f, "targetApp") is not null) continue;
                if (Str(f, "key") is { } fk && Str(f, "targetEntity") is { } target) refs[fk] = target;
            }
        }
        return index;
    }

    public static CordRefIndex FromModel(IReadOnlyList<CordEntity> entities)
    {
        var index = new CordRefIndex();
        foreach (var entity in entities)
        {
            if (entity.Key is not { } key) continue;
            var refs = index.Slot(key);
            foreach (var field in entity.FieldList)
                if (field is { Key: { } fk, Type: "reference", TargetApp: null, TargetEntity: { } target })
                    refs[fk] = target;
        }
        return index;
    }

    private Dictionary<string, string> Slot(string entity) =>
        _byEntity.TryGetValue(entity, out var existing)
            ? existing
            : _byEntity[entity] = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The one reference on <paramref name="entity"/> that points at <paramref name="target"/>, or
    /// null when there is not exactly one.
    ///
    /// <para>Used for <c>ownedBy.via</c> — "which field on the child points at its parent" — which is
    /// the same kind of question as a rollup's join and has the same answer: measured over the corpus,
    /// unique in <b>24 of 24</b> owned entities. An author saying "a period lives inside a scenario"
    /// should not then be asked which field they meant by that.</para>
    /// </summary>
    public string? SoleReferenceTo(string entity, string target)
    {
        if (!_byEntity.TryGetValue(entity, out var refs)) return null;
        string? only = null;
        foreach (var (field, points) in refs)
        {
            if (points != target) continue;
            if (only is not null) return null;
            only = field;
        }
        return only;
    }

    /// <summary>What a reference field on <paramref name="entity"/> points at, or null.</summary>
    public string? TargetOf(string entity, string field) =>
        _byEntity.TryGetValue(entity, out var refs) && refs.TryGetValue(field, out var target) ? target : null;

    /// <summary>
    /// The <c>via</c> for an aggregate, or null when it cannot be derived unambiguously.
    ///
    /// <para>Null is not an error here — it is the signal that the author has to say which reference
    /// they meant, and that <c>CordCheck</c> should ask. Returning a guess would be worse than
    /// returning nothing: an aggregate joined through the wrong reference is valid, compiles, and
    /// quietly reports the wrong number.</para>
    /// </summary>
    /// <param name="myEntity">The entity holding the computed field.</param>
    /// <param name="of">The entity being aggregated.</param>
    /// <param name="over"><c>"mine"</c>, or a reference field on <paramref name="myEntity"/>.</param>
    public string? Infer(string myEntity, string of, string over)
    {
        // What the aggregated rows must point AT. For "mine" that is this record; for a sibling
        // aggregation it is whatever the named reference points at — the shared parent.
        var target = over == CordAggregate.Mine ? myEntity : TargetOf(myEntity, over);
        if (target is null) return null;

        if (!_byEntity.TryGetValue(of, out var refs)) return null;

        string? only = null;
        foreach (var (field, points) in refs)
        {
            if (points != target) continue;
            if (only is not null) return null;   // two candidates: a coin toss, so refuse to flip it
            only = field;
        }
        return only;
    }

    /// <summary>Every reference on <paramref name="of"/> that could have been the join. For the error
    /// message when <see cref="Infer"/> declines — naming the candidates is the difference between a
    /// question the author can answer and one they cannot.</summary>
    public IReadOnlyList<string> Candidates(string myEntity, string of, string over)
    {
        var target = over == CordAggregate.Mine ? myEntity : TargetOf(myEntity, over);
        if (target is null || !_byEntity.TryGetValue(of, out var refs)) return [];
        return refs.Where(kv => kv.Value == target).Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
