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

    [Fact]
    public async Task An_effect_fills_its_tokens_from_the_record_and_the_actor()
    {
        await using var world = new World(new WorkflowDefinition(
            "copy", "Copy", "widget", WorkflowEvent.RecordCreated,
            Effects: [new UpdateRecordEffect([new EffectSet("note", "{{record.name}} by {{actor.id}}")])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "thing", Amount = 1 }, default);

        Assert.Equal("thing by tester", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

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

        Assert.Equal("seen:", after!.Name);
    }

    [Fact]
    public async Task A_range_lays_out_one_record_per_step_and_only_once()
    {
        await using var world = new World(new WorkflowDefinition(
            "months", "Lay out the months", "widget", WorkflowEvent.RecordCreated,
            Effects:
            [
                new CreateForEachEffect("widget",
                    new RangeSource("2026-01-01", "3", "month"),
                    Key: ["name"],
                    Set: [new EffectSet("name", "month-{{source.index}}"), new EffectSet("note", "{{source.date}}")]),
            ]));

        await world.Store.CreateAsync(new Widget { Name = "plan", Amount = 1 }, default);

        var laid = world.Store.Query().Where(w => w.Name!.StartsWith("month-")).OrderBy(w => w.Name).ToList();

        Assert.Equal(3, laid.Count);
        Assert.Equal(["month-1", "month-2", "month-3"], laid.Select(w => w.Name));
        Assert.Equal(["2026-01-01", "2026-02-01", "2026-03-01"], laid.Select(w => w.Note));

        await world.Store.CreateAsync(new Widget { Name = "plan again", Amount = 2 }, default);

        Assert.Equal(3, world.Store.Query().Count(w => w.Name!.StartsWith("month-")));
    }

    [Theory]
    [InlineData("{{today+1w}}", "2026-03-22")]
    [InlineData("{{today+2w}}", "2026-03-29")]
    [InlineData("{{today+7d}}", "2026-03-22")]
    [InlineData("{{today-30d}}", "2026-02-13")]
    [InlineData("{{today+7}}", "2026-03-22")]
    public async Task An_effect_writes_the_date_a_clock_offset_names(string token, string expected)
    {
        await using var world = new World(new WorkflowDefinition(
            "schedule_next", "Schedule the next one", "widget", WorkflowEvent.FieldChanged,
            Field: "name",
            Effects: [new UpdateRecordEffect([new EffectSet("note", token)])]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "open", Amount = 1 }, default);
        await world.Store.UpdateAsync(widget.Id, new Widget { Name = "done" }, ["name"], default);

        Assert.Equal(expected, (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    [Fact]
    public async Task Ticking_a_repeating_record_creates_the_next_one_a_week_out()
    {
        await using var world = new World(new WorkflowDefinition(
            "recur", "Create the next occurrence", "widget", WorkflowEvent.FieldChanged,
            Field: "name",
            Effects:
            [
                new CreateRecordEffect("widget",
                [
                    new EffectSet("name", "{{record.name}} (next)"),
                    new EffectSet("note", "{{today+1w}}"),
                ]),
            ]));

        var widget = await world.Store.CreateAsync(new Widget { Name = "water the plants", Amount = 1 }, default);
        await world.Store.UpdateAsync(widget.Id, new Widget { Name = "done" }, ["name"], default);

        var created = world.Store.Query().Single(w => w.Id != widget.Id);

        Assert.Equal("2026-03-22", created.Note);
        Assert.Equal("done (next)", created.Name);
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
