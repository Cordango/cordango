// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Integrations;

/// <param name="Notes">Not refusals: a contract field this provider cannot fill is reported, because
/// an app reading it will see it empty and somebody should know why.</param>
public sealed record AdapterOutcome(IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, JsonObject? Plan, string? Hash)
{
    public bool Ok => Errors.Count == 0 && Plan is not null;
}

/// <summary>
/// Checks a provider (<c>kind: IntegrationProvider</c> — the provider adapter for one external API)
/// against the contracts it claims to implement and compiles it to an execution plan: the provider
/// with every default written out, the hashes of the contracts it was checked against, and a hash of
/// its own. A connection pins that hash.
///
/// <para>Named for the adapter, not the provider: "provider" in this assembly would read as the AI
/// provider concept the boundary tests keep out of the compiler.</para>
///
/// <para><b>"Implements" is a promise the gate holds the provider to.</b> Every capability, filter,
/// sort and figure the contract declares must have an operation behind it. A provider that cannot
/// filter by a field cannot claim a contract that says the field is filterable, because an app
/// relying on that filter would otherwise get one unfiltered page and read it as the answer.</para>
/// </summary>
public static class AdapterCompiler
{
    public const string Kind = "IntegrationProvider";

    private static readonly IReadOnlySet<string> CredentialHeaders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "authorization", "proxy-authorization", "cookie" };

    /// <param name="catalog">True only for a provider shipped in the catalog — the one place a
    /// provider may use the platform's own OAuth client.</param>
    public static AdapterOutcome Compile(JsonNode? source, KnownIntegrations contracts, bool catalog = false)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        source = ContractCompiler.Normalized(source);
        var errors = Gate.StructuralErrors(source, IntegrationSchemas.ProviderNode);
        if (errors.Count > 0) return new AdapterOutcome(errors, [], null, null);

        var doc = (JsonObject)source!;
        var notes = new List<string>();
        var name = Str(doc, "name")!;

        var config = doc["config"] as JsonObject ?? [];
        var configKeys = config.Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, node) in config)
        {
            var c = (JsonObject)node!;
            if (Str(c, "type") == "select" && c["options"] is null)
                errors.Add($"SEMANTIC: {name} config '{key}': a select needs 'options'");
            if (Str(c, "pattern") is { } pattern && !ValidPattern(pattern))
                errors.Add($"SEMANTIC: {name} config '{key}': pattern '{pattern}' is not a regular expression");
        }
        var hostSafe = config.Where(kv => kv.Value is JsonObject c
                                          && (Str(c, "type") == "select" || HostSafePattern(Str(c, "pattern"))))
                             .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        var auth = (JsonObject)doc["auth"]!;
        var authType = Str(auth, "type")!;
        CheckAuth(auth, authType, name, catalog, errors);

        var http = (JsonObject)doc["http"]!;
        CheckBaseUrl(Str(http, "baseUrl")!, authType, configKeys, hostSafe, name, errors);
        foreach (var (header, value) in http["headers"] as JsonObject ?? [])
        {
            if (CredentialHeaders.Contains(header))
                errors.Add($"SEMANTIC: {name} http header '{header}' carries a credential — the auth block places credentials, never a fixed header");
            RequestTemplate.Check(value?.GetValue<string>() ?? "", RequestTemplate.Scope.Url, configKeys, $"{name} http header '{header}'", errors);
        }

        if (doc["test"] is JsonObject test)
        {
            CheckRequestPath(Str(test, "path")!, RequestTemplate.Scope.Url, configKeys, $"{name} test", errors);
            foreach (var (q, value) in test["query"] as JsonObject ?? [])
                RequestTemplate.Check(value?.GetValue<string>() ?? "", RequestTemplate.Scope.Url, configKeys, $"{name} test query '{q}'", errors);
            if (Str(test, "expect") is { } expect && ApiPath.TryParse(expect, out var eerr) is null)
                errors.Add($"SEMANTIC: {name} test expect: {eerr}");
        }

        var implemented = new List<IntegrationContract>();
        foreach (var reference in (doc["implements"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
        {
            if (contracts.Find(reference) is { } c) implemented.Add(c);
            else errors.Add($"SEMANTIC: {name} implements '{reference}', which is not a known contract"
                          + (contracts.References.Any() ? $" (known: {string.Join(", ", contracts.References)})" : ""));
        }

        var served = new HashSet<(string, string)>();
        var compiledEntities = new JsonArray();
        foreach (var (key, node) in ((JsonObject)doc["entities"]!).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var entity = (JsonObject)node!;
            var where = $"{name} entity '{key}'";

            IntegrationContract? contract;
            if (Str(entity, "contract") is { } cref)
            {
                contract = implemented.FirstOrDefault(c => c.Ref.ToString() == cref);
                if (contract is null)
                {
                    errors.Add($"SEMANTIC: {where}: serves '{cref}', which is not in this provider's 'implements'");
                    continue;
                }
            }
            else if (implemented.Count == 1) contract = implemented[0];
            else
            {
                if (implemented.Count > 1)
                    errors.Add($"SEMANTIC: {where}: this provider implements {implemented.Count} contracts — say which one with 'contract'");
                continue;
            }

            var entityKey = Str(entity, "entity") ?? key;
            if (contract.Entity(entityKey) is not { } target)
            {
                errors.Add($"SEMANTIC: {where}: '{contract.Ref}' has no entity '{entityKey}' "
                         + $"(it has: {string.Join(", ", contract.Entities.Select(e => e.Key))})");
                continue;
            }
            if (!served.Add((contract.Ref.ToString(), entityKey)))
                errors.Add($"SEMANTIC: {where}: '{contract.Ref}' entity '{entityKey}' is already served by another entry");

            compiledEntities.Add(CompileEntity(key, entity, contract, target, configKeys, where, errors, notes));
        }

        foreach (var c in implemented)
            foreach (var e in c.Entities.Where(e => !served.Contains((c.Ref.ToString(), e.Key))))
                errors.Add($"SEMANTIC: {name} implements '{c.Ref}' but serves none of its entity '{e.Key}' — add an 'entities' entry for it");

        if (errors.Count > 0) return new AdapterOutcome(errors, notes, null, null);

        var plan = new JsonObject
        {
            ["kind"] = "integration-plan",
            ["planVersion"] = "1.0",
            ["name"] = name,
            ["version"] = doc["version"]!.GetValue<int>(),
            ["label"] = Str(doc, "label") ?? name,
            ["implements"] = new JsonArray([.. implemented.Select(c => c.Ref.ToString()).Order(StringComparer.Ordinal).Select(r => (JsonNode)r!)]),
            ["contracts"] = new JsonObject(implemented.OrderBy(c => c.Ref.ToString(), StringComparer.Ordinal)
                .Select(c => KeyValuePair.Create(c.Ref.ToString(), (JsonNode?)c.Hash))),
            ["config"] = config.DeepClone(),
            ["http"] = Http(http),
            ["auth"] = auth.DeepClone(),
        };
        if (doc["test"] is JsonObject t) plan["test"] = WithMethod(t);
        plan["entities"] = compiledEntities;
        var hash = DefinitionHash.Of(plan);
        plan["hash"] = hash;
        return new AdapterOutcome([], notes, plan, hash);
    }

    private static JsonObject CompileEntity(string key, JsonObject entity, IntegrationContract contract,
        ContractEntity target, IReadOnlySet<string> configKeys, string where, List<string> errors, List<string> notes)
    {
        var operations = (JsonObject)entity["operations"]!;
        var compiledOps = new JsonObject();
        foreach (var (opName, opNode) in operations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var op = (JsonObject)opNode!;
            CheckOperation(opName, op, target, configKeys, $"{where} {opName}", errors);
            compiledOps[opName] = Operation(opName, op);
        }

        foreach (var capability in target.Capabilities.Order(StringComparer.Ordinal))
            if (capability is "list" or "get" && operations[capability] is null)
                errors.Add($"SEMANTIC: {where}: '{contract.Ref}' says {target.Key} can be '{capability}', and this entry has no '{capability}' operation");

        var queryOpName = Str(entity, "queryOperation") ?? (operations["search"] is not null ? "search" : "list");
        var queryOp = operations[queryOpName] as JsonObject;
        var needsQuery = target.Filters.Count > 0 || target.Sorts.Count > 0 || target.Capabilities.Contains("search");
        if (needsQuery && queryOp is null)
            errors.Add($"SEMANTIC: {where}: queryOperation '{queryOpName}' is not one of its operations");

        var mapping = (JsonObject)((JsonObject)entity["mapping"]!)["fromProvider"]!;
        var fromProvider = new JsonObject();
        foreach (var (field, value) in mapping.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var at = $"{where} mapping '{field}'";
            if (target.Field(field) is not { } cf)
            {
                errors.Add($"SEMANTIC: {at}: '{contract.Ref}' entity '{target.Key}' has no field '{field}' "
                         + $"(it has: {string.Join(", ", target.Fields.Select(f => f.Key))})");
                continue;
            }
            var (from, transform) = value switch
            {
                JsonValue v => (v.GetValue<string>(), (JsonNode?)null),
                JsonObject o => (Str(o, "from")!, o["transform"]),
                _ => ("", null),
            };
            if (ApiPath.TryParse(from, out var perr) is null) errors.Add($"SEMANTIC: {at}: {perr}");
            Transforms.Check(transform, cf, at, errors);
            var compiled = new JsonObject { ["from"] = from };
            if (transform is not null)
                compiled["transform"] = new JsonArray([.. Transforms.Steps(transform).Select(s => (JsonNode)s.DeepClone())]);
            fromProvider[field] = compiled;
        }
        foreach (var f in target.Fields.Where(f => !mapping.ContainsKey(f.Key)))
        {
            if (f.Required || f.Key == "id")
                errors.Add($"SEMANTIC: {where}: '{f.Key}' is {(f.Key == "id" ? "the row's identity" : "required by the contract")} and is not mapped");
            else
                notes.Add($"{where}: '{f.Key}' is not mapped, so it is unavailable from this provider and reads as empty");
        }

        if (queryOp is not null)
        {
            var filters = queryOp["filters"] as JsonObject;
            foreach (var (field, ops) in target.Filters)
            {
                var at = $"{where} {queryOpName}: filter '{field}'";
                if (filters?["fields"]?[field] is not JsonObject spec)
                { errors.Add($"SEMANTIC: {at}: the contract says it is filterable, and the operation does not say how"); continue; }
                var declaredOps = filters["ops"] as JsonObject;
                foreach (var op in ops.Order(StringComparer.Ordinal))
                    if (declaredOps is null ? op != "eq" : !declaredOps.ContainsKey(op))
                        errors.Add($"SEMANTIC: {at}: the contract allows '{op}', and the operation's 'ops' has no token for it");
                if (Transforms.Translates((mapping[field] as JsonObject)?["transform"]) && spec["values"] is null)
                    errors.Add($"SEMANTIC: {at}: its mapping translates values, so the filter must translate them back with 'values' "
                             + "— sent as-is it would match nothing");
            }
            var sorts = queryOp["sort"] as JsonObject;
            foreach (var field in target.Sorts.Order(StringComparer.Ordinal))
                if (sorts?["fields"]?[field] is null)
                    errors.Add($"SEMANTIC: {where} {queryOpName}: sort '{field}' — the contract says it is sortable, and the operation does not say how");
            if (target.Capabilities.Contains("search") && !TakesText(queryOp))
                errors.Add($"SEMANTIC: {where} {queryOpName}: the contract says {target.Key} can be searched — give 'text' or use {{{{search.text}}}} in the body");
        }

        var aggregate = entity["aggregate"] as JsonObject;
        if (target.Aggregates.Count)
        {
            var via = Str(aggregate?["count"] as JsonObject, "via");
            if (via is null)
                errors.Add($"SEMANTIC: {where}: the contract offers an exact count, and this entry has no 'aggregate.count'");
            else if (operations[via] is not JsonObject viaOp)
                errors.Add($"SEMANTIC: {where}: aggregate count via '{via}', which is not one of its operations");
            else if (viaOp["response"]?["total"] is null)
                errors.Add($"SEMANTIC: {where}: aggregate count via '{via}' needs that operation's response.total");
        }
        else if (aggregate?["count"] is not null)
            notes.Add($"{where}: offers a count the contract does not declare, so no app can use it");
        foreach (var (op, set) in new[] { ("sum", target.Aggregates.Sum), ("avg", target.Aggregates.Avg),
                                          ("min", target.Aggregates.Min), ("max", target.Aggregates.Max) })
            if (set.Count > 0)
                errors.Add($"SEMANTIC: {where}: the contract offers '{op}', and a provider can only compute an exact count for now");

        var result = new JsonObject
        {
            ["key"] = key,
            ["contract"] = contract.Ref.ToString(),
            ["entity"] = target.Key,
            ["queryOperation"] = queryOpName,
            ["operations"] = compiledOps,
        };
        if (aggregate is not null) result["aggregate"] = aggregate.DeepClone();
        result["fromProvider"] = fromProvider;
        return result;
    }

    private static void CheckOperation(string opName, JsonObject op, ContractEntity target,
        IReadOnlySet<string> configKeys, string where, List<string> errors)
    {
        var method = Str(op, "method") ?? "GET";
        var path = Str(op, "path")!;
        CheckRequestPath(path, opName == "get" ? RequestTemplate.Scope.GetPath : RequestTemplate.Scope.Url, configKeys, where, errors);
        if (opName == "get" && !RequestTemplate.Variables(path, out _).Contains("id"))
            errors.Add($"SEMANTIC: {where}: a 'get' path names the row with {{{{id}}}}");

        foreach (var (q, value) in op["query"] as JsonObject ?? [])
            RequestTemplate.Check(value?.GetValue<string>() ?? "", RequestTemplate.Scope.Query, configKeys, $"{where} query '{q}'", errors);

        var body = op["body"];
        if (body is not null && method == "GET")
            errors.Add($"SEMANTIC: {where}: a GET request has no body — use POST, or move the values to 'query'");
        var wholes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in Strings(body))
        {
            if (RequestTemplate.IsWhole(text, out var whole)) wholes.Add(whole!);
            else foreach (var v in RequestTemplate.Variables(text, out _).Where(v => v is "filters" or "sorts"))
                errors.Add($"SEMANTIC: {where}: {{{{{v}}}}} is replaced by a list, so it must be the whole value");
            RequestTemplate.Check(text, RequestTemplate.Scope.Body, configKeys, $"{where} body", errors);
        }

        var response = op["response"] as JsonObject;
        foreach (var (k, p) in response ?? [])
            if (ApiPath.TryParse(p?.GetValue<string>(), out var perr) is null)
                errors.Add($"SEMANTIC: {where} response {k}: {perr}");
        if (opName is "list" or "search" && response?["items"] is null)
            errors.Add($"SEMANTIC: {where}: needs response.items — where the rows are in the reply");

        if (op["paging"] is JsonObject paging)
        {
            var style = Str(paging, "style")!;
            var param = Str(paging, "param");
            var (bodyVar, label) = style switch
            {
                "cursor" => ("page.cursor", "cursor"),
                "offset" => ("page.offset", "offset"),
                "page" => ("page.number", "page number"),
                _ => ((string?)null, ""),
            };
            if (bodyVar is not null && param is null && !wholes.Contains(bodyVar) && !Strings(body).Any(s => RequestTemplate.Variables(s, out _).Contains(bodyVar)))
                errors.Add($"SEMANTIC: {where}: {style} paging needs 'param' or {{{{{bodyVar}}}}} in the body to carry the {label}");
            if (style == "cursor" && response?["nextCursor"] is null)
                errors.Add($"SEMANTIC: {where}: cursor paging needs response.nextCursor — where the next cursor is in the reply");
            if (style == "none" && (param is not null || Str(paging, "sizeParam") is not null))
                errors.Add($"SEMANTIC: {where}: paging style 'none' takes no parameters");
        }

        if (op["filters"] is JsonObject filters)
        {
            var hasQuery = filters["query"] is not null;
            var hasEach = filters["each"] is not null;
            if (hasQuery == hasEach)
                errors.Add($"SEMANTIC: {where} filters: give exactly one of 'query' (each filter a parameter) or 'each' (each filter an object in the body)");
            if (hasEach && !wholes.Contains("filters"))
                errors.Add($"SEMANTIC: {where} filters: 'each' builds a list that replaces {{{{filters}}}} in the body, and the body has none");
            foreach (var text in Strings(filters["each"]).Append(Str(filters, "query") ?? ""))
                RequestTemplate.Check(text, RequestTemplate.Scope.Filter, configKeys, $"{where} filters", errors);
            foreach (var (field, _) in filters["fields"] as JsonObject ?? [])
                if (target.Field(field) is null)
                    errors.Add($"SEMANTIC: {where} filters: '{field}' is not a field of the contract entity");
        }

        if (op["sort"] is JsonObject sort)
        {
            var hasQuery = sort["query"] is not null;
            var hasEach = sort["each"] is not null;
            if (hasQuery == hasEach)
                errors.Add($"SEMANTIC: {where} sort: give exactly one of 'query' or 'each'");
            if (hasEach && sort["directions"] is null)
                errors.Add($"SEMANTIC: {where} sort: 'each' needs 'directions' — the provider's words for ascending and descending");
            if (hasEach && !wholes.Contains("sorts"))
                errors.Add($"SEMANTIC: {where} sort: 'each' builds a list that replaces {{{{sorts}}}} in the body, and the body has none");
            foreach (var text in Strings(sort["each"]).Concat(Strings(sort["query"])))
                RequestTemplate.Check(text, RequestTemplate.Scope.Sort, configKeys, $"{where} sort", errors);
            foreach (var (field, _) in sort["fields"] as JsonObject ?? [])
                if (target.Field(field) is null)
                    errors.Add($"SEMANTIC: {where} sort: '{field}' is not a field of the contract entity");
        }
    }

    private static bool TakesText(JsonObject op) =>
        op["text"] is not null || Strings(op["body"]).Any(s => RequestTemplate.Variables(s, out _).Contains("search.text"));

    private static void CheckAuth(JsonObject auth, string type, string name, bool catalog, List<string> errors)
    {
        var where = $"{name} auth";
        if (type == "api_key" && auth["placement"] is null)
            errors.Add($"SEMANTIC: {where}: an api_key needs 'placement' — the header or query parameter that carries it");
        if (type != "api_key" && auth["placement"] is not null)
            errors.Add($"SEMANTIC: {where}: 'placement' is for an api_key; a {type} credential has a fixed place");
        var oauthKeys = new[] { "authorizeUrl", "tokenUrl", "scopes", "pkce", "client" };
        if (type == "oauth2")
        {
            foreach (var url in new[] { "authorizeUrl", "tokenUrl" })
            {
                if (Str(auth, url) is not { } value)
                    errors.Add($"SEMANTIC: {where}: oauth2 needs '{url}'");
                else if (!Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
                         || !string.IsNullOrEmpty(u.UserInfo) || value.Contains("{{", StringComparison.Ordinal))
                    errors.Add($"SEMANTIC: {where}: '{url}' must be a fixed https address — an OAuth endpoint is where credentials are sent");
            }
            if (Str(auth, "client") == "platform" && !catalog)
                errors.Add($"SEMANTIC: {where}: client 'platform' is reserved for providers in the catalog — a workspace's provider brings its own OAuth client");
        }
        else
            foreach (var k in oauthKeys.Where(k => auth[k] is not null))
                errors.Add($"SEMANTIC: {where}: '{k}' belongs to oauth2, and this is {type}");
    }

    private static void CheckBaseUrl(string baseUrl, string authType, IReadOnlySet<string> configKeys,
        IReadOnlySet<string> hostSafe, string name, List<string> errors)
    {
        var where = $"{name} http baseUrl";
        RequestTemplate.Check(baseUrl, RequestTemplate.Scope.Url, configKeys, where, errors);
        var scheme = baseUrl.StartsWith("https://", StringComparison.Ordinal) ? "https" : "http";
        if (scheme == "http" && authType != "none")
            errors.Add($"SEMANTIC: {where}: '{baseUrl}' is plain http and this provider sends a credential — use https");

        var rest = baseUrl[(scheme.Length + 3)..];
        var slash = rest.IndexOfAny(['/', '?', '#']);
        var host = slash < 0 ? rest : rest[..slash];
        foreach (var v in RequestTemplate.Variables(host, out _))
            if (v.StartsWith("config.", StringComparison.Ordinal) && !hostSafe.Contains(v["config.".Length..]))
                errors.Add($"SEMANTIC: {where}: {{{{{v}}}}} sits in the host, so it must be a select or a text with a 'pattern' "
                         + "that excludes '.', '/', '@' and ':' — otherwise a connection could point the provider anywhere");

        var probe = RequestTemplate.Variables(baseUrl, out _).Aggregate(baseUrl, (s, v) => s.Replace("{{" + v + "}}", "x", StringComparison.Ordinal));
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri))
            errors.Add($"SEMANTIC: {where}: '{baseUrl}' is not an address");
        else
        {
            if (!string.IsNullOrEmpty(uri.UserInfo))
                errors.Add($"SEMANTIC: {where}: an address with a user name or password in it — the auth block carries credentials");
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                errors.Add($"SEMANTIC: {where}: no query or fragment in the base address — operations add their own");
        }
    }

    private static void CheckRequestPath(string path, IReadOnlySet<string> scope, IReadOnlySet<string> configKeys,
        string where, List<string> errors)
    {
        RequestTemplate.Check(path, scope, configKeys, $"{where} path", errors);
        if (path.Contains("//", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal) || path.Contains('?') || path.Contains('#'))
            errors.Add($"SEMANTIC: {where} path: '{path}' must stay below the base address — no '//', '..', scheme, query or fragment");
    }

    private static bool HostSafePattern(string? pattern)
    {
        if (pattern is null || !ValidPattern(pattern)) return false;
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        try { return new[] { "a.b", "a/b", "a@b", "a:b" }.All(s => !regex.IsMatch(s)) && pattern.StartsWith('^') && pattern.EndsWith('$'); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
    }

    private static bool ValidPattern(string pattern)
    {
        try { _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static JsonObject Http(JsonObject http)
    {
        var o = (JsonObject)http.DeepClone();
        o["timeoutSeconds"] ??= 10;
        return o;
    }

    private static JsonObject WithMethod(JsonObject request)
    {
        var o = (JsonObject)request.DeepClone();
        o["method"] ??= "GET";
        return o;
    }

    private static JsonObject Operation(string opName, JsonObject op)
    {
        var o = WithMethod(op);
        o["paging"] ??= new JsonObject { ["style"] = "none" };
        if (o["paging"] is JsonObject p && Str(p, "style") != "none") p["size"] ??= 100;
        if (o["paging"] is JsonObject pp && Str(pp, "style") == "page") pp["startAt"] ??= 1;
        if (opName == "get")
        {
            o["response"] ??= new JsonObject();
            ((JsonObject)o["response"]!)["item"] ??= "$";
        }
        return o;
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return s;
                break;
            case JsonObject o:
                foreach (var (_, child) in o) foreach (var s in Strings(child)) yield return s;
                break;
            case JsonArray a:
                foreach (var child in a) foreach (var s in Strings(child)) yield return s;
                break;
        }
    }

    private static string? Str(JsonObject? o, string key) => ContractCompiler.Str(o, key);
}
