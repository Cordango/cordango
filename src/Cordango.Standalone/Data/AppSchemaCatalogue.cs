// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Standalone.Data;

/// <summary>
/// One entity, described well enough that something which has never seen this application can use
/// it: what it is called, what it holds, and what a caller may send when writing one.
/// </summary>
/// <param name="ReadSchema">Every field a reader sees, including the ones the runtime fills in.</param>
/// <param name="CreateSchema">What <c>POST</c> accepts: the fields a person may write, with the
/// mandatory ones marked. Read-only and generated fields are absent rather than optional, because a
/// client that can name them will send them.</param>
/// <param name="UpdateSchema">What <c>PATCH</c> accepts — the same fields, nothing mandatory, since
/// a partial update means exactly the keys it names.</param>
public sealed record EntitySchema(
    string Key,
    string Label,
    string LabelPlural,
    string? DisplayField,
    string ReadSchema,
    string CreateSchema,
    string UpdateSchema);

/// <summary>What running one command needs from the caller, as JSON Schema.</summary>
public sealed record CommandSchema(string Key, string Label, string Entity, string InputSchema);

/// <summary>
/// The application, described.
///
/// <para><b>Why the schemas are strings.</b> They are produced by the compiler's one field-type map
/// at BUILD time and land in generated source as literals, so the document an application serves
/// cannot disagree with the definition it was built from, and two builds of the same definition
/// produce the same bytes. Parsing them here — once, on first use — is the whole runtime cost.</para>
///
/// <para><b>And why one catalogue rather than two.</b> The OpenAPI document and the MCP server want
/// the same facts. Deriving them separately is how a REST client and an AI client end up being told
/// different things about the same field, so both read this.</para>
/// </summary>
public sealed class AppSchemaCatalogue
{
    private readonly Dictionary<string, EntitySchema> _entities;
    private readonly Dictionary<string, JsonNode> _parsed = new(StringComparer.Ordinal);

    public AppSchemaCatalogue(
        string appKey,
        string appName,
        string appVersion,
        string? description,
        IReadOnlyList<EntitySchema> entities,
        IReadOnlyList<CommandSchema> commands)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(commands);

        AppKey = appKey;
        AppName = appName;
        AppVersion = appVersion;
        Description = description;
        Entities = entities;
        Commands = commands;
        _entities = entities.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    public string AppKey { get; }
    public string AppName { get; }
    public string AppVersion { get; }
    public string? Description { get; }

    /// <summary>In definition order, so anything derived from it is stable across builds.</summary>
    public IReadOnlyList<EntitySchema> Entities { get; }

    public IReadOnlyList<CommandSchema> Commands { get; }

    /// <summary>Every entity key, for the enum on a tool parameter that names one.</summary>
    public IReadOnlyList<string> EntityKeys => [.. Entities.Select(e => e.Key)];

    public EntitySchema? Entity(string entityKey) =>
        _entities.TryGetValue(entityKey, out var entity) ? entity : null;

    public IEnumerable<CommandSchema> CommandsFor(string entityKey) =>
        Commands.Where(c => string.Equals(c.Entity, entityKey, StringComparison.Ordinal));

    /// <summary>
    /// A schema as a node, parsed once and shared.
    ///
    /// <para>Callers get a DETACHED copy. A JSON node has a parent, and handing the same instance to
    /// two callers means the second one's attempt to nest it either throws or silently reparents the
    /// first one's document — a failure that only shows up once two things ask on the same
    /// request.</para>
    /// </summary>
    public JsonNode Parse(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        lock (_parsed)
        {
            if (!_parsed.TryGetValue(schema, out var node))
            {
                node = JsonNode.Parse(schema) ?? new JsonObject();
                _parsed[schema] = node;
            }

            return node.DeepClone();
        }
    }
}
