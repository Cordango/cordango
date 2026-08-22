// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Cordango.Standalone.Commands;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hooks;
using Cordango.Standalone.Http;
using Cordango.Standalone.Notifications;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Tests;

public class CommandGuardEnforcementTests
{
    [Fact]
    public async Task A_record_that_fails_the_guard_is_refused()
    {
        await using var world = new World(Condition.Leaf("amount", "lt", "100"));
        var widget = await world.Store.CreateAsync(new Widget { Name = "Big", Amount = 500 }, default);

        var refused = await Assert.ThrowsAsync<RecordException>(
            () => world.Commands.RunAsync(widget.Id, "shrink", Input(), EntityAccess.Full, default));

        Assert.Equal("command.not_applicable", refused.Code);
        Assert.Equal(409, refused.StatusCode);

        Assert.Equal(500, (await world.Store.FindAsync(widget.Id, default))!.Amount);
    }

    [Fact]
    public async Task A_record_that_satisfies_the_guard_runs()
    {
        await using var world = new World(Condition.Leaf("amount", "lt", "100"));
        var widget = await world.Store.CreateAsync(new Widget { Name = "Small", Amount = 50 }, default);

        await world.Commands.RunAsync(widget.Id, "shrink", Input(), EntityAccess.Full, default);

        Assert.Equal("shrunk", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    [Fact]
    public async Task A_command_with_no_guard_runs_on_anything()
    {
        await using var world = new World(guard: null);
        var widget = await world.Store.CreateAsync(new Widget { Name = "Huge", Amount = 9999 }, default);

        await world.Commands.RunAsync(widget.Id, "shrink", Input(), EntityAccess.Full, default);

        Assert.Equal("shrunk", (await world.Store.FindAsync(widget.Id, default))!.Note);
    }

    [Fact]
    public async Task The_guard_reads_the_whole_record_whatever_the_caller_may_see()
    {
        await using var world = new World(Condition.Leaf("amount", "lt", "100"));
        var widget = await world.Store.CreateAsync(new Widget { Name = "Big", Amount = 500 }, default);

        var blinkered = new EntityAccess(true, true, true, true,
            fields: new Dictionary<string, FieldRule> { ["amount"] = new(Read: false, Update: false) },
            commands: new HashSet<string>(StringComparer.Ordinal) { "shrink" });

        var refused = await Assert.ThrowsAsync<RecordException>(
            () => world.Commands.RunAsync(widget.Id, "shrink", Input(), blinkered, default));

        Assert.Equal("command.not_applicable", refused.Code);
    }

    private static JsonElement Input() => JsonDocument.Parse("{}").RootElement;

    private sealed class World : IAsyncDisposable
    {
        public World(Condition? guard)
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
            Store = new RecordStore<Widget>(Db, descriptor,
                new RecordHooks<Widget>([], [], [], [], [], []), user, clock, ids);

            var catalogue = new AppCommandCatalogue(
            [
                new CommandDefinition("shrink", "Shrink", "widget",
                    Sets: [new CommandSet("note", "shrunk")],
                    When: guard),
            ]);

            Commands = new CommandService<Widget>(Store, catalogue, user, clock,
                new NotificationService(Db, clock, ids));
        }

        public TestDb Db { get; }
        public IRecordStore<Widget> Store { get; }
        public CommandService<Widget> Commands { get; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
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
