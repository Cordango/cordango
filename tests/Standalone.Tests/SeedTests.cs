// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Tests;

public class SeedTests
{
    private static readonly DateOnly Anchor = new(2026, 6, 15);

    [Fact]
    public async Task Rows_load_with_their_dates_resolved_against_the_anchor()
    {
        await using var world = new SeedWorld();

        var added = await world.Target.ApplyAsync(
        [
            new JsonObject
            {
                ["id"] = "w1",
                ["name"] = "First",
                ["amount"] = 42,
                ["due"] = "{T-30}",
                ["at"] = "{T+2}T09:00:00Z",
            },
        ], Anchor, default);

        Assert.Equal(1, added);

        var row = await world.Db.Set<SeedWidget>().SingleAsync();
        Assert.Equal("First", row.Name);
        Assert.Equal(42, row.Amount);
        Assert.Equal(new DateOnly(2026, 5, 16), row.Due);
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero), row.At);
    }

    [Fact]
    public async Task Seeding_a_table_that_already_has_rows_does_nothing()
    {
        await using var world = new SeedWorld();

        JsonArray rows() => [new JsonObject { ["id"] = "w1", ["name"] = "First" }];

        Assert.Equal(1, await world.Target.ApplyAsync(rows(), Anchor, default));
        Assert.Equal(0, await world.Target.ApplyAsync(rows(), Anchor, default));
        Assert.Equal(1, await world.Db.Set<SeedWidget>().CountAsync());
    }

    [Fact]
    public void Re_anchoring_shifts_the_whole_dataset()
    {
        var row = new JsonObject { ["a"] = "{T-10}", ["b"] = "{T}", ["c"] = "{T+10}" };

        var january = SeedRunner.Resolve(row, new DateOnly(2026, 1, 20));
        var november = SeedRunner.Resolve(row, new DateOnly(2026, 11, 20));

        Assert.Equal("2026-01-10", january["a"]!.GetValue<string>());
        Assert.Equal("2026-01-20", january["b"]!.GetValue<string>());
        Assert.Equal("2026-01-30", january["c"]!.GetValue<string>());

        Assert.Equal("2026-11-10", november["a"]!.GetValue<string>());
        Assert.Equal("2026-11-30", november["c"]!.GetValue<string>());
    }

    private sealed class SeedWidget : IRecord
    {
        public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("due")] public DateOnly? Due { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    }

    private sealed class SeedDb : CordangoDbContext
    {
        public SeedDb(DbContextOptions options, ICurrentUser user, IClock clock) : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder) => builder.Entity<SeedWidget>().HasKey(w => w.Id);
    }

    private sealed class Nobody : ICurrentUser
    {
        public string? UserId => null;
        public string? PersonId => null;
        public IReadOnlyCollection<string> RoleKeys => [];
        public bool IsAdministrator => false;
    }

    private sealed class Fixed : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class SeedWorld : IAsyncDisposable
    {
        public SeedWorld()
        {
            var options = new DbContextOptionsBuilder<SeedDb>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            Db = new SeedDb(options, new Nobody(), new Fixed());

            Target = new SeedTarget<SeedWidget>(Db, new RecordDescriptor<SeedWidget>("widget", "widget",
            [
                new("name", nameof(SeedWidget.Name), (a, b) => b.Name = a.Name),
                new("amount", nameof(SeedWidget.Amount), (a, b) => b.Amount = a.Amount),
                new("due", nameof(SeedWidget.Due), (a, b) => b.Due = a.Due),
                new("at", nameof(SeedWidget.At), (a, b) => b.At = a.At),
            ]));
        }

        public SeedDb Db { get; }
        public SeedTarget<SeedWidget> Target { get; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
