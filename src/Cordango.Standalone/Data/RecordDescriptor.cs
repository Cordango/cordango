// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;

namespace Cordango.Standalone.Data;

/// <summary>
/// One field of one entity, as the runtime needs to handle it: the key the definition and the wire
/// both use, and a way to move that single value from one instance to another.
/// </summary>
/// <param name="Key">The definition's field key, which is also the JSON property name.</param>
/// <param name="PropertyName">The CLR property, so filtering and sorting can reach it by name
/// without the emitter having to produce a comparison for every field and operator in the
/// language.</param>
/// <param name="Copy">Assigns just this field, from the first instance to the second.</param>
public sealed record RecordField<T>(string Key, string PropertyName, Action<T, T> Copy) where T : class, IRecord;

/// <summary>
/// What the runtime knows about one entity: its key, and its fields in definition order.
///
/// <para><b>Why delegates and not reflection.</b> A partial update has to write exactly the fields
/// the client sent and leave the rest alone, which means turning a JSON property name into an
/// assignment. Reflection would do it, and would also mean every write path depends on property
/// names matching at runtime, discovered on the first request that gets it wrong. The generator
/// knows both sides at build time, so it emits the assignment:</para>
///
/// <code>
/// new RecordField&lt;Expense&gt;("amount", nameof(Expense.Amount), (from, to) =&gt; to.Amount = from.Amount)
/// </code>
///
/// <para>Rename the property and the generated file stops compiling, which is when you want to hear
/// about it.</para>
/// </summary>
public sealed class RecordDescriptor<T> where T : class, IRecord, new()
{
    private readonly Dictionary<string, RecordField<T>> _byKey;

    public RecordDescriptor(string entityKey, string label, IReadOnlyList<RecordField<T>> fields)
    {
        EntityKey = entityKey;
        Label = label;
        Fields = fields;
        _byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
    }

    /// <summary>The definition's entity key — what permission grants name, and what an error message
    /// calls this thing.</summary>
    public string EntityKey { get; }

    /// <summary>The human label, for messages.</summary>
    public string Label { get; }

    /// <summary>In definition order, so anything derived from it is stable across builds.</summary>
    public IReadOnlyList<RecordField<T>> Fields { get; }

    public bool TryGetField(string key, out RecordField<T> field) => _byKey.TryGetValue(key, out field!);

    /// <summary>Every field key, for the permission layer to check a payload against.</summary>
    public IReadOnlyList<string> FieldKeys => [.. Fields.Select(f => f.Key)];

    /// <summary>
    /// A detached copy, carrying the id and every field.
    ///
    /// <para>Update hooks are handed the row as it was alongside the row as it will be, because
    /// "which fields changed" is the question workflows are built on and it cannot be asked once the
    /// tracked entity has been overwritten in place.</para>
    /// </summary>
    public T Copy(T source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new T { Id = source.Id };
        foreach (var field in Fields) field.Copy(source, copy);
        return copy;
    }

    /// <summary>Apply only the named fields of <paramref name="incoming"/> onto
    /// <paramref name="target"/>. Keys the entity does not have are ignored here — rejecting them is
    /// the caller's decision, and it has better context for the message.</summary>
    public void Apply(T incoming, T target, IEnumerable<string> fieldKeys)
    {
        ArgumentNullException.ThrowIfNull(fieldKeys);
        foreach (var key in fieldKeys)
            if (_byKey.TryGetValue(key, out var field))
                field.Copy(incoming, target);
    }
}
