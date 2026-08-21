// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// A dataset to open the application on.
///
/// <para><b>Why an empty application is a bad first impression, and not only aesthetically.</b> A
/// generated application with no rows in it cannot be evaluated: every list is empty, every chart is
/// blank, every filter appears to work, and nothing tells you whether the thing you described was
/// built correctly. Seed data is what turns "it started" into "it does what I meant".</para>
///
/// <para><b>Deterministic by construction.</b> Every value comes from a hash of
/// <c>(seed, entity, field, row)</c>, so the same definition and the same seed produce the same
/// dataset on every machine, forever. There is no clock and no random number generator anywhere in
/// here — dates are written as offsets from an anchor the runtime resolves, which is what lets a
/// dataset built in March still read sensibly in November.</para>
/// </summary>
public static class SeedEmitter
{
    /// <summary>Rows per entity. Enough to page, to group and to fill a chart; few enough that a
    /// person can read the whole table and check it.</summary>
    private const int RowsPerEntity = 24;

    public static GeneratedFile Emit(AppModel app, int seed)
    {
        ArgumentNullException.ThrowIfNull(app);

        var ids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var blocks = new JsonArray();

        // The directory first: an application's own records point at people, and a reference has to
        // have something to point at.
        blocks.Add(Directory(seed, ids));

        foreach (var entity in Ordered(app))
        {
            var rows = new JsonArray();
            var entityIds = new List<string>();

            for (var index = 0; index < RowsPerEntity; index++)
            {
                var id = Id(entity.Key, index);
                entityIds.Add(id);
                rows.Add(Row(app, entity, seed, index, id, ids));
            }

            ids[entity.Key] = entityIds;
            blocks.Add(new JsonObject { ["entity"] = entity.Key, ["rows"] = rows });
        }

        var document = new JsonObject
        {
            ["anchor"] = Anchor(seed).ToString("yyyy-MM-dd"),
            ["seed"] = seed,
            ["entities"] = blocks,
        };

        return new GeneratedFile("api/Seed/seed.json", document.ToJsonString(Pretty) + "\n");
    }

    /// <summary>
    /// Entities in an order where a reference always has something to point at.
    ///
    /// <para>A plain topological sort, with cycles broken by leaving the entity where it is: a
    /// definition may legitimately have two entities that point at each other, and the right answer
    /// there is a dataset where some references are empty rather than no dataset at all.</para>
    /// </summary>
    private static IReadOnlyList<EntityModel> Ordered(AppModel app)
    {
        var remaining = app.Entities.ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<EntityModel>();

        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(e => e.AuthoredFields
                .Where(f => f.IsReference && f.TargetApp is null && f.TargetEntity is { } t && t != e.Key)
                .All(f => done.Contains(f.TargetEntity!)));

            // A cycle. Take the first and carry on; its references simply fill in as empty.
            ready ??= remaining[0];

            order.Add(ready);
            done.Add(ready.Key);
            remaining.Remove(ready);
        }

        return order;
    }

    /// <summary>The people every application refers to. A fixed cast, so that a screenshot of one
    /// generated application and a screenshot of another are recognisably the same demo.</summary>
    private static JsonObject Directory(int seed, Dictionary<string, List<string>> ids)
    {
        var rows = new JsonArray();
        var people = new List<string>();

        for (var index = 0; index < Names.Length; index++)
        {
            var id = Id("person", index);
            people.Add(id);

            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["full_name"] = Names[index],
                ["email"] = Email(Names[index]),
                ["location"] = Pick(Locations, seed, "person", "location", index),
                ["employment_status"] = "active",
                ["has_login"] = false,
                ["created_at"] = "{T-365}T09:00:00Z",
            });
        }

        ids["person"] = people;
        return new JsonObject { ["entity"] = "person", ["rows"] = rows };
    }

    private static JsonObject Row(
        AppModel app, EntityModel entity, int seed, int index, string id, Dictionary<string, List<string>> ids)
    {
        var row = new JsonObject { ["id"] = id };
        var process = app.ProcessFor(entity.Key);

        foreach (var field in entity.AuthoredFields)
        {
            if (field.Type == "attachment" || field.Type == "json") continue;
            if (field.Computed is not null) continue;

            var value = Value(app, entity, field, process, seed, index, ids);
            if (value is not null) row[field.Key] = value;
        }

        row["created_at"] = $"{{T-{180 - (index * 7)}}}T09:00:00Z";
        return row;
    }

    private static JsonNode? Value(
        AppModel app, EntityModel entity, FieldModel field, ProcessModel? process,
        int seed, int index, Dictionary<string, List<string>> ids)
    {
        // The state field is spread across the process's states rather than left at its default, so
        // a board has columns in it and a chart has more than one bar.
        if (process is not null && field.Key == process.StateField && process.States.Count > 0)
        {
            var states = process.States.Select(s => AppModel.Str(s["key"])).Where(s => s is not null).ToList();
            return states[index % states.Count];
        }

        // A quarter of the optional fields are left empty. A dataset where every column is filled
        // hides exactly the layout problems a demo is meant to surface.
        if (!field.Required && Hash(seed, entity.Key, field.Key, index) % 4 == 3) return null;

        switch (field.Type)
        {
            case "select":
            {
                var options = field.Options.Select(o => AppModel.Str(o["value"])).Where(o => o is not null).ToList();
                if (options.Count == 0) return null;
                return options[(int)(Hash(seed, entity.Key, field.Key, index) % (uint)options.Count)];
            }

            case "multiselect":
            {
                var options = field.Options.Select(o => AppModel.Str(o["value"])).Where(o => o is not null).ToList();
                if (options.Count == 0) return new JsonArray();
                var take = 1 + (int)(Hash(seed, entity.Key, field.Key, index) % (uint)Math.Min(2, options.Count));
                return new JsonArray([.. options.Take(take).Select(o => (JsonNode)o!)]);
            }

            case "reference":
            {
                var target = field.TargetApp is not null ? "person" : field.TargetEntity;
                if (target is null || !ids.TryGetValue(target, out var pool) || pool.Count == 0) return null;
                return pool[(int)(Hash(seed, entity.Key, field.Key, index) % (uint)pool.Count)];
            }

            case "boolean":
                return Hash(seed, entity.Key, field.Key, index) % 3 == 0;

            case "integer":
                return 1 + (long)(Hash(seed, entity.Key, field.Key, index) % 40);

            case "decimal":
                return Math.Round(1 + (decimal)(Hash(seed, entity.Key, field.Key, index) % 9000) / 100m, 2);

            case "money":
                return Math.Round(12 + (decimal)(Hash(seed, entity.Key, field.Key, index) % 240000) / 100m, 2);

            case "date":
                // Spread across the year either side of the anchor, so a "this month" filter finds
                // something and a chart grouped by month has more than one column.
                return "{T" + Signed(180 - (int)(Hash(seed, entity.Key, field.Key, index) % 360)) + "}";

            case "datetime":
                return "{T" + Signed(90 - (int)(Hash(seed, entity.Key, field.Key, index) % 180)) + "}T"
                    + $"{9 + (Hash(seed, entity.Key, field.Key, index) % 8):D2}:00:00Z";

            case "email":
                return Email(Pick(Names, seed, entity.Key, field.Key, index));

            case "url":
                return "https://example.com/" + Naming.Sanitise(Pick(Nouns, seed, entity.Key, field.Key, index));

            case "phone":
                return $"+49 30 {1000 + (Hash(seed, entity.Key, field.Key, index) % 9000)}";

            case "longtext":
                return Sentence(seed, entity.Key, field.Key, index);

            default:
                return Phrase(seed, entity.Key, field.Key, index);
        }
    }

    private static string Signed(int days) => days >= 0 ? "+" + days : days.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A short noun phrase, which is what most text fields hold: a title, a subject, a description.
    ///
    /// <para>Composed from two small wordlists rather than drawn from a corpus. It reads like
    /// plausible business data at a glance and is obviously fake on a second look, which is what
    /// demo data should be — nobody should have to wonder whether a seeded customer is a real
    /// one.</para>
    /// </summary>
    private static string Phrase(int seed, string entity, string field, int index)
    {
        var adjective = Pick(Adjectives, seed, entity, field + ":a", index);
        var noun = Pick(Nouns, seed, entity, field + ":n", index);
        return char.ToUpperInvariant(adjective[0]) + adjective[1..] + " " + noun;
    }

    private static string Sentence(int seed, string entity, string field, int index) =>
        Phrase(seed, entity, field, index) + " — "
        + Pick(Clauses, seed, entity, field + ":c", index) + ".";

    private static string Email(string name) =>
        Naming.Sanitise(name).Replace('_', '.') + "@example.com";

    private static string Pick(string[] pool, int seed, string entity, string field, int index) =>
        pool[(int)(Hash(seed, entity, field, index) % (uint)pool.Length)];

    /// <summary>
    /// The one source of variation, and it is a hash rather than a generator.
    ///
    /// <para>A pseudo-random sequence would also be reproducible, and would couple every value to
    /// every value drawn before it: adding a field to an entity would change the data in every
    /// field after it. Hashing the coordinates means a change to one field moves one column.</para>
    /// </summary>
    private static uint Hash(int seed, string entity, string field, int index)
    {
        var input = Encoding.UTF8.GetBytes($"{seed} {entity} {field} {index}");
        var digest = SHA256.HashData(input);
        return BitConverter.ToUInt32(digest, 0);
    }

    private static string Id(string entity, int index) => $"{Naming.Sanitise(entity)}-{index + 1:D3}";

    /// <summary>
    /// The date the dataset is arranged around, derived from the seed.
    ///
    /// <para>Derived rather than read from the clock, because a build has to be reproducible: two
    /// runs of the same command a week apart must produce the same file. The runtime can re-anchor
    /// on load if somebody wants the data to look current.</para>
    /// </summary>
    private static DateOnly Anchor(int seed) =>
        new DateOnly(2026, 1, 1).AddDays((int)(Hash(seed, "anchor", "date", 0) % 365));

    private static readonly string[] Names =
    [
        "Mara Lindqvist", "Tomas Berg", "Aisha Rahman", "Jonas Weber", "Priya Nair",
        "Felix Moreau", "Sofia Castellano", "Daniel Okafor", "Lena Hoffmann", "Ravi Menon",
        "Clara Jensen", "Marcus Adeyemi",
    ];

    private static readonly string[] Locations = ["Berlin", "Hamburg", "Munich", "Remote", "Vienna"];

    private static readonly string[] Adjectives =
    [
        "quarterly", "regional", "annual", "internal", "external", "urgent", "routine",
        "revised", "initial", "final", "seasonal", "monthly",
    ];

    private static readonly string[] Nouns =
    [
        "review", "onboarding", "travel", "workshop", "renewal", "migration", "audit",
        "training", "conference", "equipment", "subscription", "consultation", "handover",
        "assessment", "rollout", "maintenance",
    ];

    private static readonly string[] Clauses =
    [
        "agreed at the planning meeting", "raised by the team last week",
        "carried over from the previous quarter", "requested by the department lead",
        "part of the current programme of work", "following the annual review",
    ];

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
