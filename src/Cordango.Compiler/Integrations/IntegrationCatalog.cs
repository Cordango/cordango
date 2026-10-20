// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Integrations;

/// <summary>The two integration languages, embedded beside the App Definition schema.</summary>
public static class IntegrationSchemas
{
    public static readonly string ContractJson = Schemas.LoadResource("integration-contract.schema.json");
    public static readonly string ProviderJson = Schemas.LoadResource("integration-provider.schema.json");

    public static JsonNode ContractNode => JsonNode.Parse(ContractJson)!;
    public static JsonNode ProviderNode => JsonNode.Parse(ProviderJson)!;

    /// <summary>"IntegrationContract", "IntegrationProvider", or null for anything else.</summary>
    public static string? KindOf(JsonNode? document) =>
        document is JsonObject o && o["kind"] is JsonValue v && v.TryGetValue<string>(out var kind)
            && kind is ContractCompiler.Kind or AdapterCompiler.Kind
            ? kind
            : null;
}

/// <summary>
/// The contracts and providers that ship with Cordango, read from <c>schemas/integrations</c>.
///
/// <para>Open source on purpose: a provider is a description of somebody else's public API, and a
/// workspace has to be able to check an app that uses <c>hubspot.crm@1</c> with nothing installed.
/// <see cref="Problems"/> is empty in every shipped build; a test holds it there, so a broken catalog
/// document fails CI rather than silently disappearing from every roster.</para>
/// </summary>
public static class IntegrationCatalog
{
    private static readonly Lazy<(IReadOnlyList<IntegrationContract> Contracts, IReadOnlyList<JsonObject> Providers, IReadOnlyList<string> Problems)> Loaded =
        new(Load);

    public static IReadOnlyList<IntegrationContract> Contracts => Loaded.Value.Contracts;

    /// <summary>Provider documents as authored. Compiled against a roster by whoever needs a plan.</summary>
    public static IReadOnlyList<JsonObject> Providers => Loaded.Value.Providers;

    public static IReadOnlyList<string> Problems => Loaded.Value.Problems;

    private static (IReadOnlyList<IntegrationContract>, IReadOnlyList<JsonObject>, IReadOnlyList<string>) Load()
    {
        var contracts = new List<IntegrationContract>();
        var providers = new List<JsonObject>();
        var problems = new List<string>();
        var names = typeof(IntegrationCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("integrations/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        foreach (var resource in names)
        {
            JsonNode? doc;
            try { doc = JsonNode.Parse(Schemas.LoadResource(resource)); }
            catch (System.Text.Json.JsonException e) { problems.Add($"{resource}: {e.Message}"); continue; }

            switch (IntegrationSchemas.KindOf(doc))
            {
                case ContractCompiler.Kind:
                    var outcome = ContractCompiler.Compile(doc);
                    if (outcome.Contract is { } c) contracts.Add(c);
                    else problems.AddRange(outcome.Errors.Select(e => $"{resource}: {e}"));
                    break;
                case AdapterCompiler.Kind:
                    providers.Add((JsonObject)doc!);
                    break;
                default:
                    problems.Add($"{resource}: not an IntegrationContract or IntegrationProvider");
                    break;
            }
        }
        return (contracts, providers, problems);
    }
}
