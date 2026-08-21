// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace Cordango.Definition.Blueprints;

/// <summary>A machine-addressable gate result. Legacy text remains for compatibility while callers
/// move to this shape; scope and element identity are extracted where the current validator names them.</summary>
public sealed record GateFinding(
    string Code,
    string Severity,
    string? SpecLayer,
    string? ReviewLayer,
    string? ScopeId,
    string? ElementId,
    string Message);

public static partial class BlueprintFindings
{
    public static IReadOnlyList<GateFinding> For(Blueprint blueprint)
    {
        var findings = BlueprintGate.Validate(blueprint).Select(FromMessage).ToList();
        findings.AddRange(blueprint.Unsupported.Where(x => x.BlocksPublication).Select(x =>
            new GateFinding("publication.unsupported", "blocker", null, null, x.ConceptId, x.Id, x.Reason)));
        return findings;
    }

    public static GateFinding FromMessage(string raw)
    {
        var split = raw.Split(':', 2);
        var category = split.Length == 2 ? split[0].Trim().ToLowerInvariant() : "validation";
        var message = split.Length == 2 ? split[1].Trim() : raw;
        var (spec, review) = Layers(message);
        var element = ElementId().Match(message).Value is { Length: > 0 } id ? id : null;
        var scope = element?.StartsWith("pg_", StringComparison.Ordinal) == true ? element : null;
        return new GateFinding($"blueprint.{category}.{review ?? spec ?? "general"}", "error",
            spec, review, scope, element, message);
    }

    private static (string? Spec, string? Review) Layers(string message)
    {
        var text = message.ToLowerInvariant();
        if (text.Contains("scenario")) return (SpecLayers.Scenarios, ReviewLayers.Journey);
        if (text.Contains("page") || text.Contains("surface") || text.Contains("screen"))
            return (SpecLayers.Experience, ReviewLayers.Screen);
        if (text.Contains("actor") || text.Contains("permission")) return (SpecLayers.Actors, ReviewLayers.Permissions);
        if (text.Contains("job")) return (SpecLayers.Jobs, ReviewLayers.Jobs);
        if (text.Contains("workflow") || text.Contains("transition") || text.Contains("state"))
            return (SpecLayers.Workflows, ReviewLayers.Behaviour);
        if (text.Contains("field") || text.Contains("concept") || text.Contains("relationship") || text.Contains("reference"))
            return (SpecLayers.Data, ReviewLayers.Domain);
        if (text.Contains("intent") || text.Contains("corejob")) return (SpecLayers.Intent, ReviewLayers.Intent);
        return (null, null);
    }

    [GeneratedRegex(@"\b(?:cpt|fld|rel|ref|wfl|sta|trn|actor|job|srf|pg|scn)_[a-z0-9_]+\b")]
    private static partial Regex ElementId();
}
