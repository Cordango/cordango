// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Hooks;

/// <summary>
/// What a hook is handed: the database it may read, who is writing, and what time it is.
///
/// <para>Deliberately not the HTTP context. A hook runs during a request today, during a seed run
/// and a scheduled workflow tomorrow, and a hook that reached for <c>HttpContext.User</c> would work
/// in the first case and null-reference in the other two.</para>
/// </summary>
/// <param name="Db">The same <see cref="DbContext"/> and the same transaction the write is using, so
/// a hook sees its own pending changes and cannot deadlock against itself.</param>
/// <param name="User">Who is writing.</param>
/// <param name="Clock">What time it is.</param>
public sealed record RecordContext(DbContext Db, ICurrentUser User, IClock Clock);

/// <summary>
/// Runs before a record is inserted. Change the record in place; throwing
/// <see cref="Http.RecordException"/> refuses the write with a message the caller sees.
///
/// <para><b>Hooks live in the service container, not on the entity.</b> The framework this pattern
/// came from put the hook methods on the POCO itself, which reads well until you remember that in a
/// generated application the entity file is precisely the file the generator overwrites on every
/// build. The one place the design invited you to put your logic was the one place it could not
/// survive. Here, generated hooks are their own classes with their own registrations, a
/// hand-written hook is another registration beside them, and both run.</para>
/// </summary>
public interface IBeforeCreate<in T> where T : class, IRecord
{
    Task BeforeCreateAsync(T record, RecordContext context, CancellationToken ct);
}

/// <summary>Runs after the insert has been saved. Where events and effects are dispatched from —
/// nothing here can veto a write that already happened.</summary>
public interface IAfterCreate<in T> where T : class, IRecord
{
    Task AfterCreateAsync(T record, RecordContext context, CancellationToken ct);
}

/// <summary>Runs before an update is saved, with both versions in hand: <paramref name="record"/> is
/// the tracked entity carrying the incoming changes, <paramref name="before"/> is a detached copy of
/// the row as it was. Field-changed workflows need the pair.</summary>
public interface IBeforeUpdate<in T> where T : class, IRecord
{
    Task BeforeUpdateAsync(T record, T before, RecordContext context, CancellationToken ct);
}

/// <summary>Runs after an update has been saved.</summary>
public interface IAfterUpdate<in T> where T : class, IRecord
{
    Task AfterUpdateAsync(T record, T before, RecordContext context, CancellationToken ct);
}

/// <summary>Runs before a delete. Throw to refuse it — this is where "you cannot delete a customer
/// who still has open orders" lives.</summary>
public interface IBeforeDelete<in T> where T : class, IRecord
{
    Task BeforeDeleteAsync(T record, RecordContext context, CancellationToken ct);
}

/// <summary>Runs after a delete has been saved.</summary>
public interface IAfterDelete<in T> where T : class, IRecord
{
    Task AfterDeleteAsync(T record, RecordContext context, CancellationToken ct);
}

/// <summary>
/// Every hook registered for one entity, resolved once and run in registration order.
///
/// <para>Registration order is the generator's order, which is the definition's order. It is stable
/// across builds because nothing here discovers anything: an application that emits two before-create
/// hooks runs them in the sequence its source file lists them, every time, on every machine.</para>
/// </summary>
public sealed class RecordHooks<T> where T : class, IRecord
{
    private readonly IReadOnlyList<IBeforeCreate<T>> _beforeCreate;
    private readonly IReadOnlyList<IAfterCreate<T>> _afterCreate;
    private readonly IReadOnlyList<IBeforeUpdate<T>> _beforeUpdate;
    private readonly IReadOnlyList<IAfterUpdate<T>> _afterUpdate;
    private readonly IReadOnlyList<IBeforeDelete<T>> _beforeDelete;
    private readonly IReadOnlyList<IAfterDelete<T>> _afterDelete;

    public RecordHooks(
        IEnumerable<IBeforeCreate<T>> beforeCreate,
        IEnumerable<IAfterCreate<T>> afterCreate,
        IEnumerable<IBeforeUpdate<T>> beforeUpdate,
        IEnumerable<IAfterUpdate<T>> afterUpdate,
        IEnumerable<IBeforeDelete<T>> beforeDelete,
        IEnumerable<IAfterDelete<T>> afterDelete)
    {
        _beforeCreate = [.. beforeCreate];
        _afterCreate = [.. afterCreate];
        _beforeUpdate = [.. beforeUpdate];
        _afterUpdate = [.. afterUpdate];
        _beforeDelete = [.. beforeDelete];
        _afterDelete = [.. afterDelete];
    }

    public async Task BeforeCreateAsync(T record, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _beforeCreate) await hook.BeforeCreateAsync(record, context, ct);
    }

    public async Task AfterCreateAsync(T record, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _afterCreate) await hook.AfterCreateAsync(record, context, ct);
    }

    public async Task BeforeUpdateAsync(T record, T before, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _beforeUpdate) await hook.BeforeUpdateAsync(record, before, context, ct);
    }

    public async Task AfterUpdateAsync(T record, T before, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _afterUpdate) await hook.AfterUpdateAsync(record, before, context, ct);
    }

    public async Task BeforeDeleteAsync(T record, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _beforeDelete) await hook.BeforeDeleteAsync(record, context, ct);
    }

    public async Task AfterDeleteAsync(T record, RecordContext context, CancellationToken ct)
    {
        foreach (var hook in _afterDelete) await hook.AfterDeleteAsync(record, context, ct);
    }
}
