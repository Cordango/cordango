// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>How much of one definition Cord actually models, and where it did not.</summary>
/// <param name="TotalNodes">Nodes in the lowered definition.</param>
/// <param name="RawNodes">Nodes carried by the raw overlay rather than modelled.</param>
/// <param name="RawPointers">The root of each raw fragment, sorted — the exact set the allowlist
/// pins.</param>
/// <param name="Overlaps">Pointers that are BOTH semantic and inside a raw fragment. Must always be
/// empty; see <see cref="CordCoverage"/>.</param>
public sealed record CordCoverageReport(
    string App,
    int TotalNodes,
    int RawNodes,
    IReadOnlyList<string> RawPointers,
    IReadOnlyList<string> Overlaps)
{
    /// <summary>0.0 to 1.0. A definition with nothing in it is fully covered — there was nothing to
    /// miss, and reporting 0% would make an empty document look like the worst case.</summary>
    public double Fraction => TotalNodes == 0 ? 1.0 : 1.0 - (double)RawNodes / TotalNodes;

    public double Percent => Fraction * 100.0;

    public override string ToString() =>
        $"{App,-24} {Percent,6:F1}%  ({TotalNodes - RawNodes}/{TotalNodes} nodes)"
        + (RawPointers.Count == 0 ? "" : $"  raw: {string.Join(" ", RawPointers)}");
}

/// <summary>
/// The measurement that stops the round-trip proof from being worth nothing.
///
/// <para>Round-tripping proves only that <b>nothing was lost</b>, and it is satisfied trivially on
/// day one by putting the entire document in the raw overlay. It is a safety property, not a progress
/// one. Coverage is the progress property: the share of the corpus Cord genuinely represents, which
/// has to climb slice by slice and is asserted against a floor that only ever moves up.</para>
///
/// <para>Measured on the APP DEFINITION side rather than by asking the model to account for itself.
/// The lowered document is the thing whose size we are claiming to have replaced, so counting its
/// nodes is the honest denominator and needs no bookkeeping inside CordApp.</para>
/// </summary>
public static class CordCoverage
{
    public static CordCoverageReport Of(JsonNode? definition)
    {
        var app = (definition as JsonObject)?["key"]?.GetValue<string>() ?? "(unkeyed)";
        return Of(CordLower.Lowering(CordImport.Import(definition)), app);
    }

    public static CordCoverageReport Of(CordLowering lowering, string app)
    {
        ArgumentNullException.ThrowIfNull(lowering);

        var total = CordJson.Nodes(lowering.Definition);

        var raw = 0;
        foreach (var pointer in lowering.RawPointers)
            raw += CordJson.Nodes(Resolve(lowering.Definition, pointer));

        // A pointer that is both semantic and inside a raw fragment means the model claimed to
        // understand something and then had it overwritten. That round-trips perfectly — the raw copy
        // wins, so the output is right — while scoring as covered, which would make the whole coverage
        // number decorative. It has to be impossible, not merely unlikely.
        var overlaps = lowering.SemanticPointers
            .Where(s => lowering.RawPointers.Any(r => s == r || s.StartsWith(r + "/", StringComparison.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new CordCoverageReport(
            app, total, raw,
            lowering.RawPointers.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            overlaps);
    }

    /// <summary>
    /// Coverage of ONE subtree, optionally discounting fragments that are somebody else's slice.
    ///
    /// <para>A whole-definition percentage answers "how much is left", which is the right headline and
    /// the wrong number for a floor. Entities are the domain slice's work; the <c>detail</c>,
    /// <c>peek</c> and <c>form</c> block trees hanging off them are screens, and counting those
    /// against the domain would set a floor that domain work cannot move. <paramref name="ignoring"/>
    /// takes them out so the floor measures what the slice is actually responsible for — and the
    /// headline number keeps counting them, so nothing is hidden.</para>
    /// </summary>
    /// <param name="ignoring">Pointer SEGMENTS whose subtrees are excluded from both totals, e.g.
    /// <c>["detail", "peek", "form"]</c>.</param>
    public static (int Total, int Raw) Section(CordLowering lowering, string prefix, params string[] ignoring)
    {
        ArgumentNullException.ThrowIfNull(lowering);

        var excluded = lowering.RawPointers
            .Where(p => ignoring.Any(seg => p.EndsWith("/" + seg, StringComparison.Ordinal)))
            .ToList();

        var total = CordJson.Nodes(Resolve(lowering.Definition, prefix))
                    - excluded.Sum(p => CordJson.Nodes(Resolve(lowering.Definition, p)));

        var raw = lowering.RawPointers
            .Where(p => p.StartsWith(prefix + "/", StringComparison.Ordinal) || p == prefix)
            .Where(p => !excluded.Contains(p))
            .Sum(p => CordJson.Nodes(Resolve(lowering.Definition, p)));

        return (total, raw);
    }

    /// <summary>Resolves a JSON Pointer. Only the shapes a lowering emits — no array indices yet,
    /// because a raw fragment is always rooted at a named property.</summary>
    private static JsonNode? Resolve(JsonNode? root, string pointer)
    {
        if (pointer.Length == 0) return root;
        var node = root;
        foreach (var rawSegment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }
}
