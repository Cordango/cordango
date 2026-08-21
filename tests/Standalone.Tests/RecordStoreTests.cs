// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hooks;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The generic store: hooks, tracking fields, and what a partial update actually writes.
///
/// <para>Each of these pins a defect found in the prior art this design was assessed against, and
/// each of those defects was silent — nothing threw, nothing logged, the columns were simply always
/// null or the wrong rows were written. A test is the only place they show up.</para>
/// </summary>
public class RecordStoreTests
{
    [Fact]
    public async Task Create_stamps_who_and_when_and_update_leaves_the_creation_alone()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        await using var world = new World(clock, new FakeUser("mara"));

        var created = await world.Store.CreateAsync(new Widget { Name = "First", Amount = 10 }, default);

        Assert.Equal(clock.UtcNow, created.Created);
        Assert.Equal("mara", created.CreatedBy);
        Assert.Null(created.LastModified);

        // A different person, later. The creation stamp is a fact about the past and must survive.
        clock.UtcNow = clock.UtcNow.AddDays(3);
        world.User.UserId = "tim";

        var updated = await world.Store.UpdateAsync(created.Id, new Widget { Amount = 25 }, ["amount"], default);

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), updated.Created);
        Assert.Equal("mara", updated.CreatedBy);
        Assert.Equal(clock.UtcNow, updated.LastModified);
        Assert.Equal("tim", updated.LastModifiedBy);
    }

    /// <summary>
    /// A row that already carries a creation stamp keeps it.
    ///
    /// <para>The runtime provides these values; it does not insist on them. A seeded dataset carries
    /// its own dates, and overwriting them gives every demo record the same creation instant — which
    /// makes "recently created" meaningless and a chart over creation dates a single bar.</para>
    /// </summary>
    [Fact]
    public async Task An_already_stamped_row_keeps_its_own_creation_time()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        await using var world = new World(clock, new FakeUser("mara"));

        var authored = new DateTimeOffset(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);
        var created = await world.Store.CreateAsync(
            new Widget { Name = "Imported", Created = authored, CreatedBy = "importer" }, default);

        Assert.Equal(authored, created.Created);
        Assert.Equal("importer", created.CreatedBy);
    }

    /// <summary>
    /// A client sending one field changes one field.
    ///
    /// <para>The obvious implementation attaches the incoming entity and saves it, which writes
    /// every property — including the twenty the client never mentioned, now at their default. The
    /// symptom is a form that clears fields it does not show.</para>
    /// </summary>
    [Fact]
    public async Task A_partial_update_touches_only_the_named_fields()
    {
        await using var world = new World();
        var created = await world.Store.CreateAsync(
            new Widget { Name = "First", Amount = 10, Note = "keep me" }, default);

        var updated = await world.Store.UpdateAsync(created.Id, new Widget { Amount = 42 }, ["amount"], default);

        Assert.Equal(42, updated.Amount);
        Assert.Equal("First", updated.Name);
        Assert.Equal("keep me", updated.Note);
    }

    /// <summary>A replace writes everything, including the fields the body left out. Both verbs
    /// exist so that a caller can say which they mean.</summary>
    [Fact]
    public async Task A_replace_writes_every_field()
    {
        await using var world = new World();
        var created = await world.Store.CreateAsync(
            new Widget { Name = "First", Amount = 10, Note = "keep me" }, default);

        var updated = await world.Store.UpdateAsync(
            created.Id, new Widget { Name = "Second" }, world.Store.Descriptor.FieldKeys, default);

        Assert.Equal("Second", updated.Name);
        Assert.Equal(0, updated.Amount);
        Assert.Null(updated.Note);
    }

    /// <summary>
    /// A before-create hook runs, and it runs BEFORE the row exists.
    ///
    /// <para>The prior art's store declared these methods <c>async void</c>, so the controller's
    /// save raced the hook. Refusing a write from a hook refused it after the write had happened,
    /// and a hook that threw took the process down instead of answering the request.</para>
    /// </summary>
    [Fact]
    public async Task Hooks_run_in_order_and_are_awaited()
    {
        var log = new List<string>();
        await using var world = new World(hooks: log);

        var created = await world.Store.CreateAsync(new Widget { Name = "First" }, default);
        await world.Store.UpdateAsync(created.Id, new Widget { Name = "Second" }, ["name"], default);
        await world.Store.DeleteAsync(created.Id, default);

        Assert.Equal(
            ["before-create:1", "before-create:2", "after-create", "before-update", "after-update", "before-delete", "after-delete"],
            log);
    }

    [Fact]
    public async Task A_refusing_hook_stops_the_write_and_the_row_is_not_there()
    {
        await using var world = new World();
        world.Refuse = true;

        var refused = await Assert.ThrowsAsync<RecordException>(
            () => world.Store.CreateAsync(new Widget { Id = "w1", Name = "First" }, default));

        Assert.Equal("widget.refused", refused.Code);
        Assert.Null(await world.Store.FindAsync("w1", default));
    }

    /// <summary>A before-update hook sees the row as it was and the row as it will be. Field-changed
    /// workflows are built on that pair, and it cannot be reconstructed after the fact.</summary>
    [Fact]
    public async Task An_update_hook_sees_both_versions()
    {
        await using var world = new World();
        var created = await world.Store.CreateAsync(new Widget { Name = "First", Amount = 1 }, default);

        (Widget After, Widget Before)? seen = null;
        world.OnBeforeUpdate = (after, before) => seen = (after, before);

        await world.Store.UpdateAsync(created.Id, new Widget { Amount = 99 }, ["amount"], default);

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Value.Before.Amount);
        Assert.Equal(99, seen.Value.After.Amount);
    }

    [Fact]
    public async Task A_client_chosen_id_is_kept_and_a_taken_one_is_refused()
    {
        await using var world = new World();

        var chosen = await world.Store.CreateAsync(new Widget { Id = "eur", Name = "Euro" }, default);
        Assert.Equal("eur", chosen.Id);

        var clash = await Assert.ThrowsAsync<RecordException>(
            () => world.Store.CreateAsync(new Widget { Id = "eur", Name = "Also Euro" }, default));
        Assert.Equal("record.duplicate_id", clash.Code);
        Assert.Equal(409, clash.StatusCode);
    }

    [Fact]
    public async Task An_update_cannot_change_a_records_identity()
    {
        await using var world = new World();
        var created = await world.Store.CreateAsync(new Widget { Id = "w1", Name = "First" }, default);

        var updated = await world.Store.UpdateAsync(
            created.Id, new Widget { Id = "somewhere-else", Name = "Second" }, ["name"], default);

        // Otherwise every reference pointing at w1 would be left pointing at nothing.
        Assert.Equal("w1", updated.Id);
        Assert.NotNull(await world.Store.FindAsync("w1", default));
    }

    [Fact]
    public async Task Updating_or_deleting_something_that_is_not_there_says_so()
    {
        await using var world = new World();

        var missing = await Assert.ThrowsAsync<RecordException>(
            () => world.Store.UpdateAsync("nope", new Widget(), ["name"], default));
        Assert.Equal(404, missing.StatusCode);

        var gone = await Assert.ThrowsAsync<RecordException>(() => world.Store.DeleteAsync("nope", default));
        Assert.Equal(404, gone.StatusCode);
    }

    // ---- the small world these run in ---------------------------------------------------------

    private sealed class Widget : IRecord, IHasTrackingFields
    {
        public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount")] public int Amount { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }

        public DateTimeOffset Created { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public string? LastModifiedBy { get; set; }
    }

    private sealed class TestDb : CordangoDbContext
    {
        public TestDb(DbContextOptions options, ICurrentUser user, IClock clock) : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder) => builder.Entity<Widget>().HasKey(w => w.Id);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeUser(string? id) : ICurrentUser
    {
        public string? UserId { get; set; } = id;
        public string? PersonId { get; set; }
        public IReadOnlyCollection<string> RoleKeys { get; set; } = [];
        public bool IsAdministrator { get; set; }
    }

    /// <summary>A database, a store and a place for hooks to write down that they ran.</summary>
    private sealed class World : IAsyncDisposable
    {
        public World(FixedClock? clock = null, FakeUser? user = null, List<string>? hooks = null)
        {
            Clock = clock ?? new FixedClock(DateTimeOffset.UnixEpoch);
            User = user ?? new FakeUser("someone");
            Log = hooks ?? [];

            var options = new DbContextOptionsBuilder<TestDb>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                // The in-memory provider warns that it cannot honour a transaction. True, and not
                // what these tests are about.
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            Db = new TestDb(options, User, Clock);

            var descriptor = new RecordDescriptor<Widget>("widget", "widget",
            [
                new("name", nameof(Widget.Name), (a, b) => b.Name = a.Name),
                new("amount", nameof(Widget.Amount), (a, b) => b.Amount = a.Amount),
                new("note", nameof(Widget.Note), (a, b) => b.Note = a.Note),
            ]);

            var recorder = new Recorder(this);
            var recordHooks = new RecordHooks<Widget>(
                [new FirstCreateHook(this), recorder], [recorder], [recorder], [recorder], [recorder], [recorder]);

            Store = new RecordStore<Widget>(Db, descriptor, recordHooks, User, Clock, new GuidRecordIdGenerator());
        }

        public FixedClock Clock { get; }
        public FakeUser User { get; }
        public TestDb Db { get; }
        public IRecordStore<Widget> Store { get; }
        public List<string> Log { get; }

        public bool Refuse { get; set; }
        public Action<Widget, Widget>? OnBeforeUpdate { get; set; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>Registered first, so that hook ORDER is observable rather than assumed.</summary>
    private sealed class FirstCreateHook(World world) : IBeforeCreate<Widget>
    {
        public Task BeforeCreateAsync(Widget record, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("before-create:1");
            return Task.CompletedTask;
        }
    }

    private sealed class Recorder(World world) :
        IBeforeCreate<Widget>, IAfterCreate<Widget>,
        IBeforeUpdate<Widget>, IAfterUpdate<Widget>,
        IBeforeDelete<Widget>, IAfterDelete<Widget>
    {
        public Task BeforeCreateAsync(Widget record, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("before-create:2");
            if (world.Refuse) throw new RecordException("widget.refused", "Not this one.");
            return Task.CompletedTask;
        }

        public Task AfterCreateAsync(Widget record, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("after-create");
            return Task.CompletedTask;
        }

        public Task BeforeUpdateAsync(Widget record, Widget before, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("before-update");
            world.OnBeforeUpdate?.Invoke(record, before);
            return Task.CompletedTask;
        }

        public Task AfterUpdateAsync(Widget record, Widget before, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("after-update");
            return Task.CompletedTask;
        }

        public Task BeforeDeleteAsync(Widget record, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("before-delete");
            return Task.CompletedTask;
        }

        public Task AfterDeleteAsync(Widget record, RecordContext context, CancellationToken ct)
        {
            world.Log.Add("after-delete");
            return Task.CompletedTask;
        }
    }
}
