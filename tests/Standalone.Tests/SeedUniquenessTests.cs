// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The demo dataset has to LOAD.
///
/// <para>Every value in a generated seed comes from a hash of (seed, entity, field, row) over a
/// fixed word list, and nothing consulted <c>unique</c>. Twenty-four rows drawn from a few dozen
/// phrases collide constantly — three scenarios called the same thing is the normal case — so an
/// entity with <c>unique: true</c> on its name produced a dataset the unique index rejected. The
/// application then died on startup with a constraint violation, which is a far worse first
/// impression than an empty screen: nothing renders, and the error is about a database index rather
/// than about anything the author wrote.</para>
///
/// <para>Asserted over every reference application rather than a fixture, because the collision
/// depends on how many rows share a small vocabulary, and that is a property of a real definition
/// rather than of a contrived one.</para>
/// </summary>
public class SeedUniquenessTests
{
    public static TheoryData<string> Applications()
    {
        var data = new TheoryData<string>();
        foreach (var key in new[]
        {
            "expenses", "time-off", "task-manager", "room-booking",
            "helpdesk", "sales-crm", "ventures", "budget-planner",
        }) data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(Applications))]
    public void No_unique_field_is_seeded_with_the_same_value_twice(string key)
    {
        var (definition, seed) = Build(key);
        var checked_ = 0;

        foreach (var entity in definition["entities"]!.AsArray().OfType<JsonObject>())
        {
            var entityKey = (string?)entity["key"];
            var rows = RowsOf(seed, entityKey);
            if (rows is null) continue;

            foreach (var field in entity["fields"]!.AsArray().OfType<JsonObject>())
            {
                if (field["unique"]?.GetValue<bool>() != true) continue;
                var fieldKey = (string?)field["key"];
                if (fieldKey is null) continue;

                checked_++;

                // Null is not a duplicate of null: SQL treats them as distinct, so any number of
                // rows may decline to fill in an optional unique field.
                var values = rows
                    .OfType<JsonObject>()
                    .Select(r => r[fieldKey])
                    .Where(v => v is not null)
                    .Select(v => v!.ToJsonString())
                    .ToList();

                var duplicates = values
                    .GroupBy(v => v, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key} × {g.Count()}")
                    .ToList();

                Assert.True(duplicates.Count == 0,
                    $"{key}: seeded '{entityKey}.{fieldKey}' is unique but the dataset repeats "
                    + $"{string.Join(", ", duplicates)}. The application will not start.");
            }
        }

        Assert.True(checked_ > 0 || !HasUniqueField(definition),
            $"{key}: nothing was checked, so this test is not covering what it claims to.");
    }

    private static bool HasUniqueField(JsonObject definition) =>
        definition["entities"]!.AsArray().OfType<JsonObject>()
            .SelectMany(e => e["fields"]!.AsArray().OfType<JsonObject>())
            .Any(f => f["unique"]?.GetValue<bool>() == true);

    private static JsonArray? RowsOf(JsonObject seed, string? entityKey) =>
        entityKey is null
            ? null
            : seed["entities"]?.AsArray().OfType<JsonObject>()
                .FirstOrDefault(b => (string?)b["entity"] == entityKey)?["rows"]?.AsArray();

    private static (JsonObject Definition, JsonObject Seed) Build(string key)
    {
        var corpus = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus");
        var path = Path.Combine(corpus, "reference", key + ".appdef.json");
        if (!File.Exists(path)) path = Path.Combine(corpus, key + ".appdef.json");

        var outcome = CandidateValidator.Run(
            JsonNode.Parse(File.ReadAllText(path))!.AsObject(), key,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(outcome.Manifest is not null,
            $"{key} did not compile: {string.Join("; ", outcome.Errors)}");

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        var result = new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
        {
            ["allowIncomplete"] = true,
            ["seed"] = 42,
        }));

        var file = result.Files.Single(f => f.RelativePath == "api/Seed/seed.json");
        return (outcome.Manifest!, JsonNode.Parse(file.Content)!.AsObject());
    }
}
