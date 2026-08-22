// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Microsoft.EntityFrameworkCore;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Workflows;

/// <summary>
/// Reading and writing one entity without knowing its type.
///
/// <para><b>Why this exists.</b> Everything else in this runtime is generic over the entity, which is
/// what keeps the store, the controller and the hooks written once. Workflows break that: a message
/// arriving stamps a field on its TICKET, and a scenario being created inserts rows into
/// <c>revenue_plan</c>. The code reacting to one entity has to write to another, named by a string in
/// the definition, and there is no type parameter that can carry that.</para>
///
/// <para>So each entity gets an untyped façade over its own typed store, registered as a collection
/// the way <see cref="ISeedTarget"/> already is, and the runner picks the one whose
/// <see cref="Entity"/> matches. No reflection, no service locator keyed by <c>Type</c>, and every
/// write still goes through the ordinary store — so a workflow's write fires the same hooks a
/// person's edit does.</para>
/// </summary>
public interface IEntityWriter
{
    /// <summary>The entity key as the definition spells it.</summary>
    string Entity { get; }

    /// <summary>One record as JSON, or null. JSON because the caller does not know the type and the
    /// condition evaluator reads JSON anyway.</summary>
    Task<JsonObject?> FindAsync(string id, CancellationToken ct);

    /// <summary>Insert, returning the new record. The values are field keys as the definition spells
    /// them, converted exactly as an ordinary POST of the same fields would convert them.</summary>
    Task<JsonObject> CreateAsync(JsonObject values, CancellationToken ct);

    /// <summary>Write only the named fields onto an existing record.</summary>
    Task<JsonObject?> UpdateAsync(string id, JsonObject values, IReadOnlyCollection<string> fields, CancellationToken ct);

    /// <summary>
    /// The records matching a flat list of field comparisons, ANDed.
    ///
    /// <para>Deliberately not the full condition language. These filters go to the DATABASE — the
    /// same expression builder a list request uses — and a workflow laying out a grid may read a few
    /// hundred rows to build it. Evaluating a condition tree in memory would mean loading the table
    /// first, which is the difference between a query and an outage.</para>
    /// </summary>
    Task<IReadOnlyList<JsonObject>> WhereAsync(IReadOnlyList<RecordFilter> filters, CancellationToken ct);
}

/// <summary>The one implementation, generic over the entity. Registered per entity by
/// <c>AddRecord</c>, so an application's writers are exactly its entities.</summary>
public sealed class EntityWriter<T> : IEntityWriter where T : class, IRecord, new()
{
    private readonly IRecordStore<T> _store;

    public EntityWriter(IRecordStore<T> store) => _store = store;

    public string Entity => _store.Descriptor.EntityKey;

    public async Task<JsonObject?> FindAsync(string id, CancellationToken ct) =>
        await _store.FindAsync(id, ct) is { } record ? Snapshot(record) : null;

    public async Task<JsonObject> CreateAsync(JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);

        var incoming = Materialise(values, out _);
        return Snapshot(await _store.CreateAsync(incoming, ct));
    }

    public async Task<IReadOnlyList<JsonObject>> WhereAsync(
        IReadOnlyList<RecordFilter> filters, CancellationToken ct)
    {
        var query = RecordQuery.Narrow(_store.Query(), _store.Descriptor, filters ?? []);

        // Ordered by id, so a grid built twice from the same rows comes out in the same order. A
        // database is free to return rows however it likes, and "however it likes" changes between
        // runs on the same data.
        var rows = await query.OrderBy(r => r.Id).ToListAsync(ct);
        return [.. rows.Select(Snapshot)];
    }

    public async Task<JsonObject?> UpdateAsync(
        string id, JsonObject values, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(fields);

        // Only fields this entity actually has. A definition that names one it does not is a bug the
        // gate should have caught, and passing it through would surface as an EF error naming a
        // column instead of a field.
        var known = fields.Where(f => _store.Descriptor.TryGetField(f, out _)).ToList();
        if (known.Count == 0) return null;

        var incoming = Materialise(values, out _);
        incoming.Id = id;

        return Snapshot(await _store.UpdateAsync(id, incoming, known, ct));
    }

    /// <summary>
    /// JSON to the entity type, through the same deserialiser an ordinary request uses.
    ///
    /// <para>One conversion path for one field. Two is how a workflow ends up storing
    /// <c>"2026-01-02T00:00:00"</c> in a column where an edit stores <c>2026-01-02</c>, and nothing
    /// notices until somebody sorts by it.</para>
    /// </summary>
    private static T Materialise(JsonObject values, out int count)
    {
        count = values.Count;
        return values.Deserialize<T>(Json) ?? new T();
    }

    private static JsonObject Snapshot(T record) => JsonSerializer.SerializeToNode(record, Json)?.AsObject() ?? [];

    /// <summary>Numbers read from strings as well as from JSON numbers, because a definition's
    /// <c>set</c> is text and refusing to parse "0" into a decimal would make half the language
    /// unusable from here.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}
