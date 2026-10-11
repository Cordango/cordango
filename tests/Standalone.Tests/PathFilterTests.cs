// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Tests;

public class PathFilterTests
{
    [Fact]
    public async Task A_filter_on_a_field_one_reference_away_narrows_to_the_rows_pointing_at_a_match()
    {
        await using var world = await World.Create(Grants());

        var page = await world.Lines.ListAsync([new RecordFilter("item.barcode", "eq", "222")], [], 0, 50, default);

        Assert.Equal(["l2"], Ids(page));
    }

    [Fact]
    public async Task It_combines_with_the_entitys_own_filters()
    {
        await using var world = await World.Create(Grants());

        var page = await world.Lines.ListAsync(
            [new RecordFilter("item.barcode", "in", "111|222"), new RecordFilter("quantity", "gt", "1")],
            [], 0, 50, default);

        Assert.Equal(["l2"], Ids(page));
    }

    [Fact]
    public async Task An_aggregate_follows_the_same_reference()
    {
        await using var world = await World.Create(Grants());

        var result = await world.Lines.AggregateAsync(
            "count", null, null, [new RecordFilter("item.barcode", "eq", "111")], default);

        Assert.Equal(1m, result["buckets"]![0]!["value"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_field_the_role_may_not_read_on_the_other_side_is_refused()
    {
        await using var world = await World.Create(Grants(part: [new FieldOverride("barcode", Read: false)]));

        var failure = await Assert.ThrowsAsync<RecordException>(() =>
            world.Lines.ListAsync([new RecordFilter("item.barcode", "eq", "111")], [], 0, 50, default));

        Assert.Equal("record.read_denied", failure.Code);
    }

    [Fact]
    public async Task An_entity_the_role_may_not_read_is_refused()
    {
        await using var world = await World.Create(
            new AppPermissions([new RoleDefinition("clerk", [new EntityGrant("line", Read: true)])]));

        var failure = await Assert.ThrowsAsync<RecordException>(() =>
            world.Lines.ListAsync([new RecordFilter("item.barcode", "eq", "111")], [], 0, 50, default));

        Assert.Equal("record.read_denied", failure.Code);
    }

    [Fact]
    public async Task A_reference_the_role_may_not_read_is_refused()
    {
        await using var world = await World.Create(Grants(line: [new FieldOverride("item", Read: false)]));

        var failure = await Assert.ThrowsAsync<RecordException>(() =>
            world.Lines.ListAsync([new RecordFilter("item.barcode", "eq", "111")], [], 0, 50, default));

        Assert.Equal("record.read_denied", failure.Code);
    }

    [Theory]
    [InlineData("quantity.barcode")]
    [InlineData("item.barcode.length")]
    public async Task A_path_that_cannot_be_followed_is_refused_by_name(string path)
    {
        await using var world = await World.Create(Grants());

        var failure = await Assert.ThrowsAsync<RecordException>(() =>
            world.Lines.ListAsync([new RecordFilter(path, "eq", "111")], [], 0, 50, default));

        Assert.Contains(failure.Code, new[] { "query.path_invalid", "query.field_unknown" });
    }

    private static IReadOnlyList<string> Ids(System.Text.Json.Nodes.JsonObject page) =>
        [.. page["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>())];

    private static AppPermissions Grants(
        IReadOnlyList<FieldOverride>? part = null, IReadOnlyList<FieldOverride>? line = null) =>
        new([new RoleDefinition("clerk",
        [
            new EntityGrant("part", Read: true, FieldOverrides: part),
            new EntityGrant("line", Read: true, FieldOverrides: line),
        ])]);

    private sealed class World : IAsyncDisposable
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;

        private World(ServiceProvider root)
        {
            _root = root;
            _scope = root.CreateAsyncScope();
        }

        public RecordGateway<Line> Lines => _scope.ServiceProvider.GetRequiredService<RecordGateway<Line>>();

        public static async Task<World> Create(AppPermissions permissions)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var name = Guid.NewGuid().ToString();
            services.AddDbContext<ShopDb>(o => o.UseInMemoryDatabase(name));
            services.AddScoped<CordangoDbContext>(s => s.GetRequiredService<ShopDb>());
            services.AddCordangoRuntime();
            services.AddSingleton(permissions);
            services.AddSingleton<ICurrentUser>(new Clerk());
            services.AddRecord(PartDescriptor);
            services.AddRecord(LineDescriptor);

            var world = new World(services.BuildServiceProvider());
            var db = world._scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.AddRange(
                new Part { Id = "p1", Barcode = "111" },
                new Part { Id = "p2", Barcode = "222" },
                new Line { Id = "l1", Item = "p1", Quantity = 1 },
                new Line { Id = "l2", Item = "p2", Quantity = 2 },
                new Line { Id = "l3", Item = null, Quantity = 3 });
            await db.SaveChangesAsync();
            return world;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
        }
    }

    private static readonly RecordDescriptor<Part> PartDescriptor = new("part", "Part",
        [new RecordField<Part>("barcode", nameof(Part.Barcode), (from, to) => to.Barcode = from.Barcode)]);

    private static readonly RecordDescriptor<Line> LineDescriptor = new("line", "Line",
    [
        new RecordField<Line>("item", nameof(Line.Item), (from, to) => to.Item = from.Item, "part"),
        new RecordField<Line>("quantity", nameof(Line.Quantity), (from, to) => to.Quantity = from.Quantity),
    ]);

    public sealed class Part : IRecord
    {
        public string Id { get; set; } = "";
        public string? Barcode { get; set; }
    }

    public sealed class Line : IRecord
    {
        public string Id { get; set; } = "";
        public string? Item { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class Clerk : ICurrentUser
    {
        public string? UserId => "clerk-1";
        public string? PersonId => "person-1";
        public IReadOnlyCollection<string> RoleKeys => ["clerk"];
        public bool IsAdministrator => false;
    }

    private sealed class ShopDb : CordangoDbContext
    {
        public ShopDb(DbContextOptions<ShopDb> options, ICurrentUser user, IClock clock) : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder)
        {
            builder.Entity<Part>().HasKey(e => e.Id);
            builder.Entity<Line>().HasKey(e => e.Id);
        }
    }
}
