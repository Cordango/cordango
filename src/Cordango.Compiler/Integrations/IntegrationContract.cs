// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Integrations;

/// <summary>A contract named with its major version: <c>hubspot.crm@1</c>. What an app's <c>uses</c>
/// and a provider's <c>implements</c> both write.</summary>
public sealed record IntegrationRef(string Name, int Major)
{
    public override string ToString() => $"{Name}@{Major}";

    public static IntegrationRef? TryParse(string? text)
    {
        if (text is null) return null;
        var at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1) return null;
        return int.TryParse(text[(at + 1)..], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var major) && major > 0
            ? new IntegrationRef(text[..at], major)
            : null;
    }
}

/// <summary>One typed field of a contract entity, in the app field vocabulary.</summary>
public sealed record ContractField(
    string Key,
    string Label,
    string Type,
    bool Required,
    bool ReadOnly,
    IReadOnlyList<string> Options);

/// <summary>The figures a provider computes exactly. Anything not here is refused in an app rather
/// than counted from whatever page of rows happened to arrive.</summary>
public sealed record ContractAggregates(
    bool Count,
    IReadOnlySet<string> Sum,
    IReadOnlySet<string> Avg,
    IReadOnlySet<string> Min,
    IReadOnlySet<string> Max)
{
    public static readonly ContractAggregates None = new(false, Empty, Empty, Empty, Empty);
    private static IReadOnlySet<string> Empty => new HashSet<string>(StringComparer.Ordinal);

    /// <summary>True when any figure is declared at all.</summary>
    public bool Any => Count || Sum.Count + Avg.Count + Min.Count + Max.Count > 0;

    /// <summary>True when <paramref name="op"/> over <paramref name="field"/> is declared.</summary>
    public bool Allows(string op, string? field) => op switch
    {
        "count" => Count,
        "sum" => field is not null && Sum.Contains(field),
        "avg" => field is not null && Avg.Contains(field),
        "min" => field is not null && Min.Contains(field),
        "max" => field is not null && Max.Contains(field),
        _ => false,
    };
}

public sealed record ContractEntity(
    string Key,
    string Label,
    string LabelPlural,
    string DisplayField,
    IReadOnlyList<ContractField> Fields,
    IReadOnlySet<string> Capabilities,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Filters,
    IReadOnlySet<string> Sorts,
    ContractAggregates Aggregates)
{
    public ContractField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

/// <summary>
/// A compiled integration contract: what apps may depend on, with no URL, credential or API field
/// name in it. <see cref="Compiled"/> is the canonical document <see cref="Hash"/> is taken over.
/// </summary>
public sealed record IntegrationContract(
    string Name,
    int Major,
    string Label,
    IReadOnlyList<ContractEntity> Entities,
    JsonObject Compiled,
    string Hash)
{
    public IntegrationRef Ref => new(Name, Major);

    public ContractEntity? Entity(string? key) => key is null ? null : Entities.FirstOrDefault(e => e.Key == key);
}

/// <summary>
/// The integration contracts a definition may name.
///
/// <para>Unlike apps, contracts are fully known wherever a check runs — the catalog ships inside this
/// assembly and a workspace's own contracts are on disk — so a roster built with <see cref="Of"/> is a
/// CLOSED set and an unknown contract is refused. <see cref="Unknown"/> is for a caller that cannot
/// see a workspace or a tenant: it still knows the catalog, and leaves everything else unjudged.</para>
/// </summary>
public sealed class KnownIntegrations
{
    private readonly Dictionary<string, IntegrationContract> _byRef;

    private KnownIntegrations(Dictionary<string, IntegrationContract> byRef, bool complete)
    {
        _byRef = byRef;
        Complete = complete;
    }

    /// <summary>The catalog alone, and nothing refused for not being in it.</summary>
    public static KnownIntegrations Unknown => new(CatalogOnly(), complete: false);

    /// <summary>A closed roster: these contracts plus the catalog. A contract of the same name and
    /// major as a catalog one replaces it, so a workspace can develop a newer copy locally.</summary>
    public static KnownIntegrations Of(IEnumerable<IntegrationContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var byRef = CatalogOnly();
        foreach (var c in contracts) byRef[c.Ref.ToString()] = c;
        return new KnownIntegrations(byRef, complete: true);
    }

    private static Dictionary<string, IntegrationContract> CatalogOnly()
    {
        var byRef = new Dictionary<string, IntegrationContract>(StringComparer.Ordinal);
        foreach (var c in IntegrationCatalog.Contracts) byRef[c.Ref.ToString()] = c;
        return byRef;
    }

    /// <summary>True when a reference this roster does not hold is a mistake rather than unjudged.</summary>
    public bool Complete { get; }

    public IntegrationContract? Find(string? reference) =>
        reference is not null && _byRef.TryGetValue(reference, out var c) ? c : null;

    public IEnumerable<string> References => _byRef.Keys.Order(StringComparer.Ordinal);
}
