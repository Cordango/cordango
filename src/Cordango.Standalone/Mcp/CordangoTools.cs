// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cordango.Standalone.Mcp;

/// <summary>
/// What an AI client may do with this application.
///
/// <para><b>Eight tools, not eight per entity.</b> Every one of them takes the entity as an argument
/// rather than being generated for it. An application with twenty entities would otherwise publish
/// eighty tools, and a client pays for that list on every single request — so the cost of
/// <c>tools/list</c> is a fact about this application's SIZE rather than about the work being asked
/// for. Here it is constant, and <c>describe_app</c> is how a client learns the fields.</para>
///
/// <para><b>Nothing here is new capability.</b> Each tool is one operation that already exists on the
/// REST facade, reached through the same <see cref="IRecordGateway"/> — so the same
/// <c>EntityAccess</c> decides it and the same <c>RecordVisibility</c> masks the answer. An MCP
/// caller acting as a person reaches exactly what that person reaches, and a field their role may
/// not read is absent here for the same reason it is absent there.</para>
///
/// <para><b>The tool list is the same for everybody.</b> Enforcement happens when a tool is CALLED,
/// not by hiding tools from <c>tools/list</c>. A caller-dependent list cannot be cached — the
/// protocol hands out <c>ttlMs</c> and <c>cacheScope</c> on that response — and it would leak
/// something anyway: which tools are missing says what your role is.</para>
/// </summary>
[McpServerToolType]
public sealed class CordangoTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>An empty object, for a command that takes no input. A default <see cref="JsonElement"/>
    /// is <c>Undefined</c> rather than empty, and everything downstream reads that as a malformed
    /// body rather than an absent one.</summary>
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    private readonly IEnumerable<IRecordGateway> _gateways;
    private readonly AppSchemaCatalogue _schema;

    public CordangoTools(IEnumerable<IRecordGateway> gateways, AppSchemaCatalogue schema)
    {
        _gateways = gateways;
        _schema = schema;
    }

    [McpServerTool(Name = "describe_app", ReadOnly = true, Title = "Describe this application")]
    [Description(
        "What this application contains and what YOU may do with it: every entity, its fields and "
        + "their types as JSON Schema, the commands each one accepts, and your own permissions on "
        + "each. Call this first. Field names in every other tool are the keys returned here.")]
    public JsonObject DescribeApp()
    {
        var entities = new JsonArray();

        foreach (var entity in _schema.Entities)
        {
            var gateway = Find(entity.Key, required: false);

            // An entity in the catalogue with no gateway is one whose controller and registration
            // somebody removed after generating. Describing it would promise a route that answers
            // 404.
            if (gateway is null) continue;

            var access = gateway.Access;

            var commands = new JsonArray();
            foreach (var command in _schema.CommandsFor(entity.Key))
            {
                if (!access.CanRunCommand(command.Key)) continue;

                commands.Add(new JsonObject
                {
                    ["key"] = command.Key,
                    ["label"] = command.Label,
                    ["input"] = _schema.Parse(command.InputSchema),
                });
            }

            entities.Add(new JsonObject
            {
                ["key"] = entity.Key,
                ["label"] = entity.Label,
                ["labelPlural"] = entity.LabelPlural,
                ["displayField"] = entity.DisplayField,
                ["youMay"] = new JsonObject
                {
                    ["read"] = access.Read,
                    ["create"] = access.Create,
                    ["update"] = access.Update,
                    ["delete"] = access.Delete,
                },
                ["fields"] = _schema.Parse(entity.ReadSchema),
                ["createAccepts"] = _schema.Parse(entity.CreateSchema),
                ["updateAccepts"] = _schema.Parse(entity.UpdateSchema),
                ["commands"] = commands,
            });
        }

        return new JsonObject
        {
            ["key"] = _schema.AppKey,
            ["name"] = _schema.AppName,
            ["version"] = _schema.AppVersion,
            ["description"] = _schema.Description,
            ["entities"] = entities,
            ["notes"] = "Field values use the definition's own keys, so spent_on rather than spentOn. "
                + "A field your role may not read is absent from a record rather than null.",
        };
    }

    [McpServerTool(Name = "list_records", ReadOnly = true, Title = "List records")]
    [Description(
        "A page of records, newest constraints first. Filters are 'field:operator:value' strings and "
        + "combine with AND — for example 'status:eq:open' or 'amount:gt:100'. Operators: eq, neq, "
        + "gt, gte, lt, lte, in, notIn, contains, startsWith, isEmpty, isNotEmpty. Sort is a "
        + "comma-separated list of field keys where a leading minus means descending. Returns "
        + "{items, total, skip, take}, where total counts everything matching rather than this page.")]
    public Task<JsonObject> ListRecords(
        [Description("Entity key.")] string entity,
        [Description("Filters such as ['status:eq:open'].")] string[]? filter = null,
        [Description("For example '-created_at,name'.")] string? sort = null,
        [Description("Records to skip.")] int skip = 0,
        [Description("Records to return. At most 500.")] int take = 50,
        CancellationToken ct = default) =>
        Guard(() => Find(entity).ListAsync(
            RecordQuery.ParseFilters(filter), RecordQuery.ParseSort(sort), skip, take, ct));

    [McpServerTool(Name = "get_record", ReadOnly = true, Title = "Get one record")]
    [Description("One record by id, with the fields your role may read.")]
    public Task<JsonObject> GetRecord(
        [Description("Entity key.")] string entity,
        [Description("The record's id.")] string id,
        CancellationToken ct = default) =>
        Guard(() => Find(entity).GetAsync(id, ct));

    [McpServerTool(Name = "aggregate_records", ReadOnly = true, Title = "Count or total records")]
    [Description(
        "One figure, or one per group, computed by the database. Use this rather than listing "
        + "records and adding them up. groupBy also accepts 'month_of:<field>' for a date field. "
        + "Returns {op, buckets:[{key, value}]}.")]
    public Task<JsonObject> AggregateRecords(
        [Description("Entity key.")] string entity,
        [Description("count, sum, avg, min or max.")] string op = "count",
        [Description("The field to total. Needed by everything except count.")] string? field = null,
        [Description("A field key, or 'month_of:<field>'.")] string? groupBy = null,
        [Description("Filters, as on list_records.")] string[]? filter = null,
        CancellationToken ct = default) =>
        Guard(() => Find(entity).AggregateAsync(op, field, groupBy, RecordQuery.ParseFilters(filter), ct));

    [McpServerTool(Name = "create_record", Destructive = false, Title = "Create a record")]
    [Description(
        "Add one record. 'values' holds the field keys from describe_app's createAccepts schema for "
        + "this entity. Fields the application fills in itself — the id, who created it, anything "
        + "computed — are not accepted and must not be sent.")]
    public Task<JsonObject> CreateRecord(
        [Description("Entity key.")] string entity,
        [Description("Field keys and values.")] JsonElement values,
        CancellationToken ct = default) =>
        Guard(() => Find(entity).CreateAsync(values, ct));

    [McpServerTool(Name = "update_record", Destructive = true, Idempotent = true, Title = "Update a record")]
    [Description(
        "Change some fields of one record. Only the keys present in 'values' are written; everything "
        + "else is left as it was. To clear a field, send it explicitly as null.")]
    public Task<JsonObject> UpdateRecord(
        [Description("Entity key.")] string entity,
        [Description("The record's id.")] string id,
        [Description("Only the fields to change.")] JsonElement values,
        CancellationToken ct = default) =>
        Guard(() =>
        {
            var gateway = Find(entity);
            return gateway.WriteAsync(id, values, gateway.SuppliedKeys(values), ct);
        });

    [McpServerTool(Name = "delete_record", Destructive = true, Title = "Delete a record")]
    [Description("Remove one record. Ask the person you are acting for before calling this.")]
    public Task<JsonObject> DeleteRecord(
        [Description("Entity key.")] string entity,
        [Description("The record's id.")] string id,
        CancellationToken ct = default) =>
        Guard(async () =>
        {
            await Find(entity).DeleteAsync(id, ct);
            return new JsonObject { ["deleted"] = id };
        });

    [McpServerTool(Name = "run_command", Destructive = true, Title = "Run a command")]
    [Description(
        "Run one of the application's own actions against a record — approving, closing, assigning. "
        + "A command is not an update with a different name: it has its own permission and its own "
        + "rule about which states it may run from, and it may write fields you could not write "
        + "directly. describe_app lists the commands available on each entity and what input each "
        + "one needs.")]
    public Task<JsonObject> RunCommand(
        [Description("Entity key.")] string entity,
        [Description("The record's id.")] string id,
        [Description("The command key.")] string command,
        [Description("The command's input, if it needs any.")] JsonElement? input = null,
        CancellationToken ct = default) =>
        Guard(async () =>
        {
            var result = await Find(entity).RunCommandAsync(id, command, input ?? Empty, ct);
            return new JsonObject { ["record"] = result.Record, ["message"] = result.Message };
        });

    /// <summary>
    /// The gateway for one entity key.
    ///
    /// <para>An unknown key is answered by NAMING the ones that exist. A model that guessed
    /// <c>"invoices"</c> for <c>"invoice"</c> can correct itself from that in one turn; "not found"
    /// costs it a <c>describe_app</c> round trip to learn what it already nearly knew.</para>
    /// </summary>
    private IRecordGateway Find(string entity, bool required = true)
    {
        var gateway = _gateways.FirstOrDefault(g => string.Equals(g.Entity, entity, StringComparison.Ordinal));
        if (gateway is not null || !required) return gateway!;

        throw new McpException(
            $"This application has no entity '{entity}'. It has: "
            + string.Join(", ", _gateways.Select(g => g.Entity).Order(StringComparer.Ordinal)) + ".");
    }

    /// <summary>
    /// Turn a refusal into something a model can act on.
    ///
    /// <para>The stable code travels with the sentence — <c>record.create_denied</c> next to "your
    /// role may not create Expenses" — because the code is what a client can branch on and the
    /// sentence is what makes it obvious the answer is final rather than a bad request worth
    /// retrying. Anything that is NOT a decision this application made is left to propagate: it is a
    /// bug, and the error middleware's rule about not narrating those to callers applies here for
    /// the same reason.</para>
    /// </summary>
    private static async Task<JsonObject> Guard(Func<Task<JsonObject>> operation)
    {
        try
        {
            return await operation();
        }
        catch (RecordException refusal)
        {
            throw new McpException($"{refusal.Code}: {refusal.Message}");
        }
    }
}
