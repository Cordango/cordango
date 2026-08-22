// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hooks;
using Cordango.Standalone.Notifications;
using Cordango.Standalone.Records;
using Cordango.Standalone.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordango.Standalone.Tests;

/// <summary>
/// What a workflow does when a record is written, and — more importantly — what it does not do.
///
/// <para>Every test here is a rule that is invisible until it is wrong. A <c>field.changed</c>
/// workflow that fires on every save rewrites values people corrected by hand; a <c>setIfEmpty</c>
/// that does not check overwrites the stamp it was meant to protect; a rule that writes a field the
/// value it already holds turns two harmless workflows into a loop.</para>
/// </summary>
public class WorkflowRunnerTests
{
    [Fact]
    public async Task A_field_changed_workflow_fires_when_the_value_actually_changed()
    {
        await using var world = new World(new WorkflowDefinition(
            "stage_probability", "Stage probability", "widget", WorkflowEvent.FieldChanged,
            Field: "name",
            Effects: [new UpdateRecordEffect([new EffectSet("note", "moved")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "lead", Amount = 1 }, default);
        await world.Store.UpdateAsync(widget.Id, new Widget { Name = "won" }, ["name"], default);

        Assert.Equal("moved", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    /// <summary>
    /// Writing a field the value it already has is not a change.
    ///
    /// <para>Saving a form resends every field. A workflow keyed on <c>field.changed</c> that fired
    /// on every save would rewrite the probability somebody had just corrected by hand — and the
    /// person would watch their edit vanish with nothing to explain it.</para>
    /// </summary>
    [Fact]
    public async Task A_field_changed_workflow_does_not_fire_when_the_value_was_merely_rewritten()
    {
        await using var world = new World(new WorkflowDefinition(
            "stage_probability", "Stage probability", "widget", WorkflowEvent.FieldChanged,
            Field: "name",
            Effects: [new UpdateRecordEffect([new EffectSet("note", "moved")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "lead", Amount = 1 }, default);
        await world.Store.UpdateAsync(widget.Id, new Widget { Name = "lead" }, ["name"], default);

        Assert.Null((await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    [Fact]
    public async Task A_condition_that_does_not_hold_stops_the_workflow()
    {
        await using var world = new World(new WorkflowDefinition(
            "big_only", "Big only", "widget", WorkflowEvent.RecordCreated,
            When: Condition.Leaf("amount", "gt", "1000"),
            Effects: [new UpdateRecordEffect([new EffectSet("note", "big")])]));

        var small = await world.Store.CreateAsync(new Widget { Name = "a", Amount = 5 }, default);
        var big = await world.Store.CreateAsync(new Widget { Name = "b", Amount = 5000 }, default);

        Assert.Null((await world.Store.FindAsync(small.Id, default))!.Note);
        Assert.Equal("big", (await world.Store.FindAsync(big.Id, default))!.Note);
    }

    /// <summary>
    /// <c>setIfEmpty</c> is what makes "stamp the first reply" mean the FIRST one.
    /// </summary>
    [Fact]
    public async Task Set_if_empty_leaves_a_value_that_is_already_there()
    {
        await using var world = new World(new WorkflowDefinition(
            "stamp", "Stamp", "widget", WorkflowEvent.RecordUpdated,
            Effects: [new UpdateRecordEffect([new EffectSet("note", "stamped")], SetIfEmpty: true)]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "a", Amount = 1, Note = "written by hand" }, default);
        await world.Store.UpdateAsync(widget.Id, new Widget { Amount = 2 }, ["amount"], default);

        Assert.Equal("written by hand", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    /// <summary>The token grammar reaches effects: a workflow can copy from the record that
    /// triggered it, and can stamp who did it.</summary>
    [Fact]
    public async Task An_effect_fills_its_tokens_from_the_record_and_the_actor()
    {
        await using var world = new World(new WorkflowDefinition(
            "copy", "Copy", "widget", WorkflowEvent.RecordCreated,
            Effects: [new UpdateRecordEffect([new EffectSet("note", "{{record.name}} by {{actor.id}}")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "thing", Amount = 1 }, default);

        Assert.Equal("thing by tester", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    /// <summary>
    /// Two workflows that write to each other terminate, rather than running until the request dies.
    ///
    /// <para>Completing at all is the assertion. Bounded by depth, and caught earlier than that by
    /// the no-op rule — an effect writing a field the value it already holds is skipped entirely,
    /// which makes the commonest accidental cycle impossible rather than merely finite.</para>
    /// </summary>
    [Fact]
    public async Task Two_workflows_writing_to_each_other_terminate()
    {
        await using var world = new World(
            new WorkflowDefinition("ping", "Ping", "widget", WorkflowEvent.RecordUpdated,
                Effects: [new UpdateRecordEffect([new EffectSet("note", "{{record.name}}")])]),
            new WorkflowDefinition("pong", "Pong", "widget", WorkflowEvent.RecordUpdated,
                Effects: [new UpdateRecordEffect([new EffectSet("name", "{{record.note}}")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "a", Amount = 1 }, default);

        await world.Store.UpdateAsync(widget.Id, new Widget { Amount = 2 }, ["amount"], default);

        Assert.NotNull(await world.Store.FindAsync(widget.Id, default));
    }

    /// <summary>
    /// Both workflows read the record as the WRITE left it, not as the workflow before them left it.
    ///
    /// <para>The rule, stated as a test because it is the one place a reader would otherwise have to
    /// infer it. <c>copy_name</c> writes the name into the note; <c>copy_note</c> writes the note
    /// into a third field. Under a shared snapshot <c>copy_note</c> reads the note as it was — blank
    /// — so the two rules do not depend on which is listed first.</para>
    ///
    /// <para>The alternative, re-reading between workflows, reads as more helpful and is worse: two
    /// rules written independently by two people would silently depend on their order in a file
    /// neither of them chose. Chaining is still expressible, and more honestly — a workflow's write
    /// raises its own event, and the rule that wants the new value watches for THAT.</para>
    /// </summary>
    [Fact]
    public async Task A_workflow_does_not_see_what_the_workflow_before_it_wrote()
    {
        await using var world = new World(
            new WorkflowDefinition("copy_name", "Copy name", "widget", WorkflowEvent.RecordCreated,
                Effects: [new UpdateRecordEffect([new EffectSet("note", "{{record.name}}")])]),
            new WorkflowDefinition("copy_note", "Copy note", "widget", WorkflowEvent.RecordCreated,
                Effects: [new UpdateRecordEffect([new EffectSet("name", "seen:{{record.note}}")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "a", Amount = 1 }, default);
        var after = await world.Store.FindAsync(widget.Id, default);

        // The note was blank when the batch started, so the second workflow saw a blank — not "a".
        Assert.Equal("seen:", after!.Name);
    }

    private sealed class World : IAsyncDisposable
    {
        public World(params WorkflowDefinition[] workflows)
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
            var user = new FakeUser("tester");

            var options = new DbContextOptionsBuilder<TestDb>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            Db = new TestDb(options, user, clock);

            var descriptor = new RecordDescriptor<Widget>("widget", "widget",
            [
                new("name", nameof(Widget.Name), (a, b) => b.Name = a.Name),
                new("amount", nameof(Widget.Amount), (a, b) => b.Amount = a.Amount),
                new("note", nameof(Widget.Note), (a, b) => b.Note = a.Note),
            ]);

            var ids = new GuidRecordIdGenerator();

            // The runner needs a writer for the entity, and the writer needs the store the hook
            // writes through — so the store is built first with a hook that reaches back for it.
            var runner = new Lazy<WorkflowRunner>(() => new WorkflowRunner(
                new AppWorkflowCatalogue(workflows),
                [new EntityWriter<Widget>(Store!)],
                new NotificationService(Db, clock, ids),
                user, clock, new WorkflowDepth(), NullLogger<WorkflowRunner>.Instance));

            var hook = new LazyHook(runner, descriptor);

            Store = new RecordStore<Widget>(Db, descriptor,
                new RecordHooks<Widget>([], [hook], [], [hook], [], []), user, clock, ids);
        }

        public TestDb Db { get; }
        public IRecordStore<Widget> Store { get; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>
    /// Stands in for the container, resolving the runner only when a write asks for it.
    ///
    /// <para>The same deferral <see cref="WorkflowHook{T}"/> relies on, and for the same reason: a
    /// workflow writes through stores and every store notifies workflows, so the graph is a ring
    /// and something has to be fetched late. Needing this here was the first sign of it; the
    /// container refusing to build the ring at startup was the second.</para>
    /// </summary>
    private sealed class LazyHook(Lazy<WorkflowRunner> runner, RecordDescriptor<Widget> descriptor)
        : IAfterCreate<Widget>, IAfterUpdate<Widget>, IServiceProvider
    {
        public object? GetService(Type serviceType) => runner.Value;

        public Task AfterCreateAsync(Widget record, RecordContext context, CancellationToken ct) =>
            new WorkflowHook<Widget>(this, descriptor).AfterCreateAsync(record, context, ct);

        public Task AfterUpdateAsync(Widget record, Widget before, RecordContext context, CancellationToken ct) =>
            new WorkflowHook<Widget>(this, descriptor).AfterUpdateAsync(record, before, context, ct);
    }

    private sealed class TestDb(DbContextOptions options, ICurrentUser user, IClock clock)
        : CordangoDbContext(options, user, clock)
    {
        protected override void ConfigureModel(ModelBuilder builder)
        {
            builder.Entity<Widget>().HasKey(w => w.Id);
            builder.Entity<Notification>().HasKey(n => n.Id);
        }
    }

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

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeUser(string? id) : ICurrentUser
    {
        public string? UserId { get; set; } = id;
        public string? PersonId { get; set; } = id;
        public bool IsAdministrator { get; set; }
        public IReadOnlyCollection<string> RoleKeys { get; set; } = [];
    }
}
