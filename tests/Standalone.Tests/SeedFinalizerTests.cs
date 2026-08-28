// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Tests;

/// <summary>
/// What a seed load leaves for something else to finish.
///
/// <para>Seeding writes through the <c>DbContext</c> rather than the store, and that is deliberate:
/// letting a workflow react to two hundred inserts would send two hundred notifications about
/// records nobody created. But a rollup column is written by NOTHING else, so the same decision left
/// every generated application seeding itself into a state where every total it was built to
/// demonstrate read as a dash — and went on reading as one until somebody edited a record by
/// hand.</para>
///
/// <para>So the bookkeeping half of what a hook does is separated from the reacting half, and only
/// the bookkeeping runs again at the end.</para>
/// </summary>
public class SeedFinalizerTests
{
    [Fact]
    public async Task A_load_that_inserted_rows_runs_the_finalizers()
    {
        await using var world = new World();

        await SeedRunner.RunAsync(world.Services, world.File(Rows), null);

        Assert.Equal(1, world.Settled.Runs);
        Assert.Equal(2, await world.Db.Set<Widget>().CountAsync());
    }

    [Fact]
    public async Task A_load_that_inserted_nothing_leaves_them_alone()
    {
        // A second run over tables that already have rows changes nothing, and recomputing every
        // total in the application to discover that is a slow way back to where you started.
        await using var world = new World();

        await SeedRunner.RunAsync(world.Services, world.File(Rows), null);
        await SeedRunner.RunAsync(world.Services, world.File(Rows), null);

        Assert.Equal(1, world.Settled.Runs);
    }

    [Fact]
    public async Task A_missing_seed_file_runs_nothing()
    {
        await using var world = new World();

        await SeedRunner.RunAsync(
            world.Services, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), null);

        Assert.Equal(0, world.Settled.Runs);
    }

    /// <summary>
    /// The emitted half, over the corpus: an application has a finalizer exactly when it has totals.
    ///
    /// <para>Asserted as a PAIR rather than against a list of which application is which. The two
    /// files come from two conditions, and a test naming the applications would keep passing if
    /// those conditions drifted apart — which is the failure that matters here, because what goes
    /// missing is a registration nobody notices until a demo shows dashes.</para>
    /// </summary>
    [Theory]
    [InlineData("task-manager")]
    [InlineData("budget-planner")]
    [InlineData("payroll")]
    [InlineData("invoicing")]
    [InlineData("expenses")]
    [InlineData("room-booking")]
    [InlineData("time-off")]
    [InlineData("helpdesk")]
    public void A_finalizer_is_emitted_exactly_when_there_are_totals(string key)
    {
        var files = Build(key);

        var totals = files.SingleOrDefault(f => f.RelativePath == "api/Computed/AppRollups.cs");
        var finalizer = files.SingleOrDefault(f => f.RelativePath == "api/Computed/SeedRollups.cs");
        var setup = files.Single(f => f.RelativePath == "api/AppSetup.cs").Content;

        Assert.Equal(totals is null, finalizer is null);

        if (totals is null)
        {
            Assert.DoesNotContain("ISeedFinalizer", setup, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("public static async Task RecomputeAllAsync(", totals.Content, StringComparison.Ordinal);
        Assert.Contains("services.AddScoped<ISeedFinalizer, SeedRollups>();", setup, StringComparison.Ordinal);
        Assert.Contains("using Cordango.Standalone.Data;", setup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deepest first, and NOT through the per-record cascade.
    ///
    /// <para>A total is a query, so a figure counting rows that are themselves totals has to run
    /// after them or it reads what they held before. And the cascade walks UP from one row, which is
    /// right for one write and quadratic for a whole table — a project worked out once per task
    /// underneath it.</para>
    /// </summary>
    [Fact]
    public void The_recompute_visits_each_level_once()
    {
        var rollups = Build("task-manager")
            .Single(f => f.RelativePath == "api/Computed/AppRollups.cs").Content;

        var all = rollups[rollups.IndexOf("RecomputeAllAsync(", StringComparison.Ordinal)..];

        // Every aggregating entity, each read as a whole table exactly once.
        Assert.Equal(1, Occurrences(all, "await db.Set<Project>().ToListAsync(ct)"));
        Assert.Equal(1, Occurrences(all, "await db.Set<Milestone>().ToListAsync(ct)"));

        // And no walk upward from the rows it has just settled.
        Assert.DoesNotContain("AfterTaskRecordAsync", all, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static IReadOnlyList<GeneratedFile> Build(string key)
    {
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");

        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, key + " did not compile.");

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 })).Files;
    }

    private static JsonArray Rows =>
    [
        new JsonObject { ["id"] = "w1", ["name"] = "First" },
        new JsonObject { ["id"] = "w2", ["name"] = "Second" },
    ];

    private sealed class Widget : IRecord
    {
        public string Id { get; set; } = "";

        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class WidgetDb : CordangoDbContext
    {
        public WidgetDb(DbContextOptions options, ICurrentUser user, IClock clock) : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder) => builder.Entity<Widget>().HasKey(w => w.Id);
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

    private sealed class Counter : ISeedFinalizer
    {
        public int Runs { get; private set; }

        public Task RunAsync(CancellationToken ct)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly List<string> _written = [];

        public World()
        {
            var options = new DbContextOptionsBuilder<WidgetDb>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            Db = new WidgetDb(options, new Nobody(), new Fixed());

            var target = new SeedTarget<Widget>(Db, new RecordDescriptor<Widget>("widget", "widget",
            [
                new("name", nameof(Widget.Name), (a, b) => b.Name = a.Name),
            ]));

            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<ISeedTarget>(target);
            collection.AddSingleton<ISeedFinalizer>(Settled);
            Services = collection.BuildServiceProvider();
        }

        public WidgetDb Db { get; }

        public IServiceProvider Services { get; }

        public Counter Settled { get; } = new();

        /// <summary>A seed document on disk, because a path is what the runner takes.</summary>
        public string File(JsonArray rows)
        {
            var path = Path.Combine(Path.GetTempPath(), "cordango-seed-" + Guid.NewGuid() + ".json");
            System.IO.File.WriteAllText(path, new JsonObject
            {
                ["anchor"] = "2026-06-15",
                ["entities"] = new JsonArray(new JsonObject { ["entity"] = "widget", ["rows"] = rows }),
            }.ToJsonString());
            _written.Add(path);
            return path;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var path in _written) System.IO.File.Delete(path);
            await Db.DisposeAsync();
        }
    }
}
