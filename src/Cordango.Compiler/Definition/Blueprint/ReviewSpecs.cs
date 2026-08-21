// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>User-facing review layers. These are separate from persisted spec ownership layers.</summary>
public static class ReviewLayers
{
    public const string Identity = "identity";
    public const string Intent = "intent";
    public const string Domain = "domain";
    public const string Behaviour = "behaviour";
    public const string Permissions = "permissions";
    public const string Jobs = "jobs";
    public const string Architecture = "architecture";
    public const string Views = "views";
    public const string Screen = "screen";
    public const string Navigation = "navigation";
    public const string Journey = "journey";

    /// <summary>The one review of the finished app. Not a layer of a description — the app exists by
    /// the time this is asked, so the decision is made against the thing itself.</summary>
    public const string App = "app";

    /// <summary>The layers a THREE-STEP run actually uses: the Brief, then the app.
    /// <para>The rest are kept as constants so existing drafts mid-flight still deserialize and old
    /// review rows still have a name, but nothing new is filed under them. They go with the proposers
    /// that produced them.</para></summary>
    public static readonly string[] Active = [Identity, App];

    public static readonly string[] All =
        [Identity, Intent, Domain, Behaviour, Permissions, Jobs, Architecture, Views, Screen, Navigation, Journey, App];
    public static string Key(string layer, string? scopeId = null) => scopeId is null ? layer : $"{layer}:{scopeId}";
}

public static class ReviewStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Approved = "approved";
    public const string Stale = "stale";
    public const string ChangesRequested = "changes_requested";
    public static readonly string[] All = [Pending, Ready, Approved, Stale, ChangesRequested];
}

public static class ChangeRequestStatuses
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";
    public static readonly string[] All = [Open, Resolved, Dismissed];
}

public sealed record ReviewState
{
    [JsonPropertyName("key")] public string Key { get; init; } = "";
    [JsonPropertyName("layer")] public string Layer { get; init; } = "";
    [JsonPropertyName("scopeId")] public string? ScopeId { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = ReviewStatuses.Pending;
    [JsonPropertyName("contentHash")] public string ContentHash { get; init; } = "";
    [JsonPropertyName("dependencyHash")] public string DependencyHash { get; init; } = "";
    [JsonPropertyName("approvedRevision")] public int? ApprovedRevision { get; init; }
    [JsonPropertyName("approvedBy")] public string? ApprovedBy { get; init; }
    [JsonPropertyName("approvedAt")] public DateTimeOffset? ApprovedAt { get; init; }
}

public sealed record BlueprintChangeRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("reviewKey")] public string ReviewKey { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = ChangeRequestStatuses.Open;
    [JsonPropertyName("createdRevision")] public int CreatedRevision { get; init; }
    [JsonPropertyName("resolvedRevision")] public int? ResolvedRevision { get; init; }
}

/// <summary>Canonical property-level projections and the explicit review dependency graph.</summary>
public static class ReviewFlow
{
    public static Blueprint Reconcile(Blueprint blueprint) => Reconcile(null, blueprint);

    public static Blueprint Reconcile(Blueprint? previous, Blueprint blueprint)
    {
        // Explicit review mutations arrive on the candidate document; ordinary content patches
        // carry the previous states forward unchanged. Prefer the candidate when it has states so
        // an approval is not overwritten while the store reconciles the new revision.
        var source = blueprint.Reviews.Count > 0 ? blueprint.Reviews : previous?.Reviews ?? [];
        var prior = source.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var keys = Keys(blueprint);
        var hashes = keys.ToDictionary(k => k.Key, k => ContentHash(blueprint, k.Layer, k.ScopeId), StringComparer.Ordinal);
        var states = new List<ReviewState>(keys.Count);

        foreach (var key in keys)
        {
            var contentHash = hashes[key.Key];
            var dependencyText = string.Join('\n', Dependencies(blueprint, key.Layer, key.ScopeId)
                .Where(hashes.ContainsKey).Select(k => $"{k}:{hashes[k]}").Order(StringComparer.Ordinal));
            var dependencyHash = Hash(dependencyText);
            if (!prior.TryGetValue(key.Key, out var old))
            {
                states.Add(new ReviewState
                {
                    Key = key.Key, Layer = key.Layer, ScopeId = key.ScopeId,
                    Status = HasContent(blueprint, key.Layer, key.ScopeId) ? ReviewStatuses.Ready : ReviewStatuses.Pending,
                    ContentHash = contentHash, DependencyHash = dependencyHash,
                });
                continue;
            }

            var changed = old.ContentHash != contentHash || old.DependencyHash != dependencyHash;
            var openRequest = blueprint.ChangeRequests.Any(c => c.ReviewKey == key.Key && c.Status == ChangeRequestStatuses.Open);
            var status = openRequest ? ReviewStatuses.ChangesRequested
                : old.Status == ReviewStatuses.ChangesRequested
                    ? (HasContent(blueprint, key.Layer, key.ScopeId) ? ReviewStatuses.Ready : ReviewStatuses.Pending)
                : changed && old.Status == ReviewStatuses.Approved ? ReviewStatuses.Stale
                : changed ? (HasContent(blueprint, key.Layer, key.ScopeId) ? ReviewStatuses.Ready : ReviewStatuses.Pending)
                : old.Status;
            states.Add(old with
            {
                Layer = key.Layer, ScopeId = key.ScopeId, Status = status,
                ContentHash = contentHash, DependencyHash = dependencyHash,
                ApprovedRevision = changed ? null : old.ApprovedRevision,
                ApprovedBy = changed ? null : old.ApprovedBy,
                ApprovedAt = changed ? null : old.ApprovedAt,
            });
        }
        return blueprint with { Reviews = states };
    }

    public static (Blueprint Blueprint, IReadOnlyList<string> Errors) Approve(
        Blueprint blueprint, string reviewKey, string actorId, DateTimeOffset now)
    {
        var reconciled = Reconcile(blueprint);
        var state = reconciled.Reviews.FirstOrDefault(x => x.Key == reviewKey);
        if (state is null) return (blueprint, [$"REVIEW: '{reviewKey}' is not a review in this blueprint"]);
        if (state.Status == ReviewStatuses.Pending)
            return (blueprint, [$"REVIEW: '{reviewKey}' has not been generated yet"]);
        if (reconciled.ChangeRequests.Any(c => c.ReviewKey == reviewKey && c.Status == ChangeRequestStatuses.Open))
            return (blueprint, [$"REVIEW: resolve the open change request for '{reviewKey}' before approving it"]);

        var unapproved = Dependencies(reconciled, state.Layer, state.ScopeId)
            .Select(k => reconciled.Reviews.FirstOrDefault(x => x.Key == k))
            .Where(x => x is not null && x.Status != ReviewStatuses.Approved)
            .Select(x => x!.Key).ToList();
        if (unapproved.Count > 0)
            return (blueprint, [$"REVIEW: approve {string.Join(", ", unapproved)} first"]);

        var nextRevision = reconciled.Revision + 1;
        return (reconciled with
        {
            Revision = nextRevision, ParentRevision = reconciled.Revision,
            Reviews = reconciled.Reviews.Select(x => x.Key == reviewKey ? x with
            {
                Status = ReviewStatuses.Approved, ApprovedRevision = nextRevision,
                ApprovedBy = actorId, ApprovedAt = now,
            } : x).ToList(),
        }, []);
    }

    public static Blueprint RequestChanges(Blueprint blueprint, string reviewKey, string message)
    {
        var reconciled = Reconcile(blueprint);
        if (reconciled.Reviews.All(x => x.Key != reviewKey))
            throw new ArgumentException($"'{reviewKey}' is not a review in this blueprint", nameof(reviewKey));
        var revision = reconciled.Revision + 1;
        var request = new BlueprintChangeRequest
        {
            Id = $"chg_{Guid.NewGuid():N}", ReviewKey = reviewKey, Message = message.Trim(),
            Status = ChangeRequestStatuses.Open, CreatedRevision = revision,
        };
        return Reconcile(reconciled, reconciled with
        {
            Revision = revision, ParentRevision = reconciled.Revision,
            ChangeRequests = [.. reconciled.ChangeRequests, request],
        });
    }

    public static Blueprint ResolveChangeRequest(Blueprint blueprint, string requestId, bool dismiss)
    {
        if (blueprint.ChangeRequests.All(x => x.Id != requestId))
            throw new ArgumentException($"change request '{requestId}' does not exist", nameof(requestId));
        var revision = blueprint.Revision + 1;
        var requests = blueprint.ChangeRequests.Select(x => x.Id == requestId ? x with
        {
            Status = dismiss ? ChangeRequestStatuses.Dismissed : ChangeRequestStatuses.Resolved,
            ResolvedRevision = revision,
        } : x).ToList();
        return Reconcile(blueprint, blueprint with
        {
            Revision = revision, ParentRevision = blueprint.Revision, ChangeRequests = requests,
        });
    }

    public static bool AllApproved(Blueprint blueprint) => Reconcile(blueprint).Reviews
        .Where(x => HasContent(blueprint, x.Layer, x.ScopeId)).All(x => x.Status == ReviewStatuses.Approved);

    public static IReadOnlyList<string> Dependencies(Blueprint bp, string layer, string? scopeId) => layer switch
    {
        ReviewLayers.Identity => [],
        ReviewLayers.Intent => [ReviewLayers.Identity],
        ReviewLayers.Domain => [ReviewLayers.Intent],
        ReviewLayers.Behaviour => [ReviewLayers.Domain],
        ReviewLayers.Permissions => [ReviewLayers.Domain, ReviewLayers.Behaviour],
        ReviewLayers.Jobs => [ReviewLayers.Domain, ReviewLayers.Behaviour, ReviewLayers.Permissions],
        ReviewLayers.Architecture => [ReviewLayers.Intent, ReviewLayers.Domain, ReviewLayers.Behaviour, ReviewLayers.Permissions, ReviewLayers.Jobs],
        ReviewLayers.Views when scopeId is not null => [ReviewLayers.Architecture],
        ReviewLayers.Screen when scopeId is not null => [ReviewLayers.Key(ReviewLayers.Views, scopeId)],
        ReviewLayers.Navigation => [ReviewLayers.Architecture, .. PageKeys(bp, ReviewLayers.Screen)],
        ReviewLayers.Journey => [ReviewLayers.Navigation, ReviewLayers.Behaviour, ReviewLayers.Permissions],
        _ => [],
    };

    public static string ContentHash(Blueprint bp, string layer, string? scopeId = null)
    {
        object slice = layer switch
        {
            // Identity is the NAME and the URL, and nothing else.
            //
            // Hashing the whole AppSpec meant editing a description or picking an icon marked an
            // already-approved name as stale and sent the user back to re-approve it. Worse once the
            // URL is reserved on approval: re-approving would look like a handle change.
            ReviewLayers.Identity => new { bp.App.Name, bp.App.Key },
            // Everything else about what the app IS belongs to the overview review, including the
            // description that used to sit on the other side of this line.
            ReviewLayers.Intent => new { bp.App.Description, bp.App.Icon, bp.Intent },
            ReviewLayers.Domain => new
            {
                bp.Concepts, bp.Externals, bp.Relationships, bp.References,
                Fields = bp.Data?.Fields.Where(f => f.Provenance != FieldProvenance.DerivedFromWorkflow).ToList() ?? [],
            },
            ReviewLayers.Behaviour => new
            {
                bp.Workflows,
                Fields = bp.Data?.Fields.Where(f => f.Provenance == FieldProvenance.DerivedFromWorkflow).ToList() ?? [],
            },
            ReviewLayers.Permissions => bp.Actors,
            ReviewLayers.Jobs => bp.Jobs,
            ReviewLayers.Architecture => new
            {
                Pages = (bp.Experience?.Pages ?? []).Select(p => new { p.Id, p.Key, p.Label, p.Icon, p.Role }),
                Surfaces = (bp.Experience?.Surfaces ?? []).Select(s => new { s.Id, s.Key, s.Label, s.Surface, s.ConceptId, s.Scope, s.ScopeConceptId }),
            },
            ReviewLayers.Views => ViewSlice(bp, scopeId),
            ReviewLayers.Screen => ScreenSlice(bp, scopeId),
            ReviewLayers.Navigation => new { Pages = bp.Experience?.Pages.Select(p => new { p.Id, p.Key, p.Label, p.Icon, p.Role }), bp.Experience?.Landing },
            ReviewLayers.Journey => bp.Scenarios,
            _ => new { },
        };
        return Hash(JsonSerializer.Serialize(slice, BlueprintJson.Options));
    }

    private static object ViewSlice(Blueprint bp, string? pageId)
    {
        var page = bp.Experience?.Page(pageId);
        var ids = page?.SurfaceIds.ToHashSet(StringComparer.Ordinal) ?? [];
        return new { PageId = pageId, Surfaces = (bp.Experience?.Surfaces ?? []).Where(s => ids.Contains(s.Id)) };
    }

    private static object ScreenSlice(Blueprint bp, string? pageId)
    {
        var page = bp.Experience?.Page(pageId);
        return page is null ? new { PageId = pageId, SurfaceIds = Array.Empty<string>(), Brief = (string?)null }
            : new { PageId = pageId, page.SurfaceIds, page.Brief };
    }

    private static bool HasContent(Blueprint bp, string layer, string? scopeId) => layer switch
    {
        ReviewLayers.Identity => !string.IsNullOrWhiteSpace(bp.App.Name) && bp.App.Name != "Untitled app",
        ReviewLayers.Intent => !string.IsNullOrWhiteSpace(bp.Intent.CoreJob),
        ReviewLayers.Domain => bp.Concepts.Count > 0 && (bp.Data?.Fields.Count ?? 0) > 0,
        ReviewLayers.Behaviour => bp.Workflows.Count > 0,
        ReviewLayers.Permissions => bp.Actors.Count > 0,
        ReviewLayers.Jobs => bp.Jobs.Count > 0,
        ReviewLayers.Architecture => (bp.Experience?.Pages.Count ?? 0) > 0,
        ReviewLayers.Views or ReviewLayers.Screen => bp.Experience?.Page(scopeId) is not null,
        ReviewLayers.Navigation => (bp.Experience?.Pages.Count ?? 0) > 0,
        ReviewLayers.Journey => bp.Scenarios.Count > 0,
        _ => false,
    };

    private static List<(string Key, string Layer, string? ScopeId)> Keys(Blueprint bp)
    {
        var result = new List<(string, string, string?)>
        {
            (ReviewLayers.Identity, ReviewLayers.Identity, null), (ReviewLayers.Intent, ReviewLayers.Intent, null),
            (ReviewLayers.Domain, ReviewLayers.Domain, null), (ReviewLayers.Behaviour, ReviewLayers.Behaviour, null),
            (ReviewLayers.Permissions, ReviewLayers.Permissions, null), (ReviewLayers.Jobs, ReviewLayers.Jobs, null),
            (ReviewLayers.Architecture, ReviewLayers.Architecture, null),
        };
        foreach (var page in bp.Experience?.Pages ?? [])
        {
            result.Add((ReviewLayers.Key(ReviewLayers.Views, page.Id), ReviewLayers.Views, page.Id));
            result.Add((ReviewLayers.Key(ReviewLayers.Screen, page.Id), ReviewLayers.Screen, page.Id));
        }
        result.Add((ReviewLayers.Navigation, ReviewLayers.Navigation, null));
        result.Add((ReviewLayers.Journey, ReviewLayers.Journey, null));
        return result;
    }

    private static IEnumerable<string> PageKeys(Blueprint bp, string layer) =>
        (bp.Experience?.Pages ?? []).Select(p => ReviewLayers.Key(layer, p.Id));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
