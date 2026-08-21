// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Hooks;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Data;

/// <summary>Reading and writing one entity's rows, hooks included.</summary>
public interface IRecordStore<T> where T : class, IRecord, new()
{
    RecordDescriptor<T> Descriptor { get; }

    /// <summary>Unfiltered and untracked, for the caller to filter, sort and page. Untracked because
    /// a list is a read: tracking a page of rows nobody will write costs memory and gives the change
    /// tracker a chance to save something an unrelated call put there.</summary>
    IQueryable<T> Query();

    Task<T?> FindAsync(string id, CancellationToken ct);

    Task<T> CreateAsync(T record, CancellationToken ct);

    /// <summary>Apply <paramref name="fieldKeys"/> of <paramref name="incoming"/> to the stored row.
    /// Only the named fields move, so a client that sends three fields does not blank the other
    /// twenty.</summary>
    Task<T> UpdateAsync(string id, T incoming, IReadOnlyCollection<string> fieldKeys, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>
/// The one implementation, generic over the entity — written once here rather than emitted per
/// entity, because none of it varies with the entity except the parts
/// <see cref="RecordDescriptor{T}"/> already carries.
///
/// <para><b>Every hook is awaited.</b> Worth saying because the prior art's equivalent methods were
/// <c>async void</c>: the controller called create, did not await it, and saved while the
/// before-create hook was still running — so a hook that refused a write refused it after the write,
/// and its exception crashed the process instead of answering the request. Anything here that
/// returns a <see cref="Task"/> is awaited before the next step begins.</para>
/// </summary>
public sealed class RecordStore<T> : IRecordStore<T> where T : class, IRecord, new()
{
    private readonly DbContext _db;
    private readonly RecordHooks<T> _hooks;
    private readonly RecordContext _context;
    private readonly IRecordIdGenerator _ids;

    public RecordStore(
        CordangoDbContext db,
        RecordDescriptor<T> descriptor,
        RecordHooks<T> hooks,
        ICurrentUser user,
        IClock clock,
        IRecordIdGenerator ids)
    {
        _db = db;
        Descriptor = descriptor;
        _hooks = hooks;
        _ids = ids;
        _context = new RecordContext(db, user, clock);
    }

    public RecordDescriptor<T> Descriptor { get; }

    public IQueryable<T> Query() => _db.Set<T>().AsNoTracking();

    public Task<T?> FindAsync(string id, CancellationToken ct) =>
        _db.Set<T>().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<T> CreateAsync(T record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A client may choose the id — handles like "eur" or "berlin-office" are legitimate keys and
        // the definition allows them. It just may not choose one that is taken.
        if (string.IsNullOrWhiteSpace(record.Id)) record.Id = _ids.NewId();
        else if (await _db.Set<T>().AnyAsync(e => e.Id == record.Id, ct))
            throw new RecordException("record.duplicate_id",
                $"A {Descriptor.Label} with id '{record.Id}' already exists.", 409);

        await _hooks.BeforeCreateAsync(record, _context, ct);

        _db.Set<T>().Add(record);
        await _db.SaveChangesAsync(ct);

        await _hooks.AfterCreateAsync(record, _context, ct);
        return record;
    }

    public async Task<T> UpdateAsync(string id, T incoming, IReadOnlyCollection<string> fieldKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var stored = await FindAsync(id, ct)
            ?? throw RecordException.NotFound(Descriptor.EntityKey, id);

        // Taken before anything is applied: once the tracked entity has the new values on it, the
        // old ones are gone and "which fields changed" can no longer be asked.
        var before = Descriptor.Copy(stored);

        Descriptor.Apply(incoming, stored, fieldKeys);

        // The id is not a field and is never moved by Apply. Changing a row's identity through an
        // update would orphan every reference pointing at it.
        stored.Id = id;

        await _hooks.BeforeUpdateAsync(stored, before, _context, ct);
        await _db.SaveChangesAsync(ct);
        await _hooks.AfterUpdateAsync(stored, before, _context, ct);

        return stored;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var stored = await FindAsync(id, ct)
            ?? throw RecordException.NotFound(Descriptor.EntityKey, id);

        await _hooks.BeforeDeleteAsync(stored, _context, ct);

        _db.Set<T>().Remove(stored);
        await _db.SaveChangesAsync(ct);

        await _hooks.AfterDeleteAsync(stored, _context, ct);
    }
}

/// <summary>Where a new record's id comes from when the client did not supply one.</summary>
public interface IRecordIdGenerator
{
    string NewId();
}

/// <summary>The default: a compact uuid. An application that wants readable keys — invoice numbers,
/// slugs — replaces this registration with its own.</summary>
public sealed class GuidRecordIdGenerator : IRecordIdGenerator
{
    public string NewId() => Guid.NewGuid().ToString("n");
}
