// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.SourceGen.Common;

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
public static partial class SeedEmitter
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
        foreach (var block in Directory(seed, ids)) blocks.Add(block);

        foreach (var entity in Ordered(app))
        {
            var rows = new JsonArray();
            var entityIds = new List<string>();

            // What each unique field has already handed out. A dataset is only useful if it LOADS,
            // and two rows carrying the same value in a `unique: true` column do not — the index
            // rejects the insert and the application dies on startup with a constraint violation
            // rather than an empty screen, which is a much worse first impression than no data.
            var taken = Taken(entity);

            for (var index = 0; index < RowsPerEntity; index++)
            {
                var id = Id(entity.Key, index);
                var row = Row(app, entity, seed, index, id, ids, taken);

                // A required unique field that has run out of distinct values ends the entity here
                // rather than emitting a row that cannot be stored. Twenty rows that load beat
                // twenty-four that do not.
                if (row is null) break;

                entityIds.Add(id);
                rows.Add(row);
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
    private static List<JsonObject> Directory(int seed, Dictionary<string, List<string>> ids)
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

        return
        [
            new JsonObject { ["entity"] = "person", ["rows"] = rows },
            Companies(seed, ids),
            Contacts(seed, ids),
        ];
    }

    /// <summary>
    /// The companies every application refers to.
    ///
    /// <para>Seeded for the same reason the people are, and it was missed for a while: an
    /// application whose records point at a customer had nothing to point AT, so every Company
    /// column in a generated demo was empty and the reference read as a field nobody had filled in
    /// rather than as one with no rows to offer.</para>
    /// </summary>
    private static JsonObject Companies(int seed, Dictionary<string, List<string>> ids)
    {
        var rows = new JsonArray();
        var organizations = new List<string>();

        for (var index = 0; index < CompanyNames.Length; index++)
        {
            var id = Id("organization", index);
            organizations.Add(id);

            var name = CompanyNames[index];
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["status"] = "active",
                ["industry"] = Pick(Industries, seed, "organization", "industry", index),
                ["website"] = $"https://{Slug(name)}.example",
                ["email"] = $"hello@{Slug(name)}.example",
                ["city"] = Pick(Locations, seed, "organization", "city", index),
                ["country"] = "DE",
                ["created_at"] = "{T-300}T09:00:00Z",
            });
        }

        ids["organization"] = organizations;
        return new JsonObject { ["entity"] = "organization", ["rows"] = rows };
    }

    /// <summary>One named person at each company — the external counterpart a deal is done with,
    /// which is not the same list as the colleagues in <see cref="Names"/>.</summary>
    private static JsonObject Contacts(int seed, Dictionary<string, List<string>> ids)
    {
        var rows = new JsonArray();
        var contacts = new List<string>();
        var organizations = ids.GetValueOrDefault("organization") ?? [];

        for (var index = 0; index < ContactNames.Length && index < organizations.Count; index++)
        {
            var id = Id("contact", index);
            contacts.Add(id);

            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["full_name"] = ContactNames[index],
                ["organization"] = organizations[index],
                ["job_title"] = Pick(JobTitles, seed, "contact", "job_title", index),
                ["email"] = Email(ContactNames[index]),
                ["is_primary"] = true,
                ["status"] = "active",
                ["created_at"] = "{T-280}T09:00:00Z",
            });
        }

        ids["contact"] = contacts;
        return new JsonObject { ["entity"] = "contact", ["rows"] = rows };
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-').Replace("--", "-", StringComparison.Ordinal);
    }

    /// <summary>One row's worth of values, or null when a required unique field has nothing
    /// distinct left to give.</summary>
    private static JsonObject? Row(
        AppModel app, EntityModel entity, int seed, int index, string id,
        Dictionary<string, List<string>> ids, Dictionary<string, HashSet<string>> taken)
    {
        var row = new JsonObject { ["id"] = id };
        var process = app.ProcessFor(entity.Key);

        foreach (var field in entity.AuthoredFields)
        {
            if (field.Type == "attachment" || field.Type == "json") continue;
            if (field.Computed is not null) continue;
            // A field the RUNTIME owns is not one a demo row may claim. `readOnly` covers the
            // generated ones — a public token, an owner stamped from the caller — and inventing a
            // value for those is worse than leaving them empty: a public address that reads
            // "Urgent handover" looks like a link somebody chose, and it can never resolve.
            if (field.ReadOnly) continue;

            var value = Value(app, entity, field, process, seed, index, ids);

            if (field.Unique && taken.TryGetValue(field.Key, out var used))
            {
                value = Distinct(value, field, used);
                if (value is null && field.Required) return null;
                // An optional unique field is left empty instead. SQL treats nulls as distinct from
                // each other, so any number of rows may decline to fill one in.
                if (value is not null) used.Add(value.ToJsonString());
            }

            if (value is not null) row[field.Key] = value;
        }

        row["created_at"] = $"{{T-{180 - (index * 7)}}}T09:00:00Z";
        return row;
    }

    /// <summary>The unique fields of an entity, each with an empty ledger.</summary>
    private static Dictionary<string, HashSet<string>> Taken(EntityModel entity) =>
        entity.AuthoredFields
            .Where(f => f.Unique && f.Computed is null)
            .ToDictionary(f => f.Key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// The same value, made distinct — or null when it cannot be.
    ///
    /// <para>Values here come from a hash of (seed, entity, field, row) over a fixed word list, so
    /// twenty-four rows of a text field collide constantly: three scenarios called "Regional
    /// rollout" is the norm rather than bad luck.</para>
    ///
    /// <para><b>Every attempt is checked, not just the first.</b> Appending the row index produced a
    /// value that was very probably free and never asked whether it was, which is the same bug one
    /// layer down. Each type now walks candidates until one is actually unused, so the answer is
    /// guaranteed rather than likely.</para>
    ///
    /// <para><b>Varied, but not random.</b> A random suffix would do the job and would also end
    /// determinism, which is load-bearing here: two builds of one definition are asserted to be
    /// byte-identical, and a dataset that changed every build would make every diff unreadable. So
    /// the discriminator is the attempt number, which behaves like a randomiser and is reproducible.
    /// </para>
    ///
    /// <para>Some types genuinely run out. A boolean has two values, a select has as many as the
    /// author wrote, and a reference has as many as the target has rows — no suffix can be added to
    /// any of them without writing something the column does not allow. A unique field of one of
    /// those stops the entity early rather than being handed a value the index would reject.</para>
    /// </summary>
    private static JsonNode? Distinct(JsonNode? value, FieldModel field, HashSet<string> used)
    {
        if (value is null) return null;
        if (!used.Contains(value.ToJsonString())) return value;

        // Generous, because the ceiling is only reached when every candidate collides — which for
        // anything but a closed value set means the entity is far larger than a demo dataset.
        const int Attempts = RowsPerEntity * 8;

        switch (field.Type)
        {
            case "boolean":
            case "select":
            case "multiselect":
            case "reference":
                return null;

            case "integer":
            {
                var number = value.GetValue<long>();
                return Walk(Attempts, bump => number + bump, used);
            }

            case "decimal":
            case "money":
            {
                var number = value.GetValue<decimal>();
                return Walk(Attempts, bump => number + (bump * 0.01m), used);
            }

            case "email":
            {
                // The tag goes before the @, because "ada.lovelace@example.com 2" is not an address.
                var text = value.GetValue<string>();
                var at = text.IndexOf('@', StringComparison.Ordinal);
                if (at <= 0) return null;
                return Walk(Attempts, bump => $"{text[..at]}+{bump}{text[at..]}", used);
            }

            case "date":
            case "datetime":
            {
                // Written as a token the runtime resolves — "{T-97}" or "{T+12}T09:00:00Z" — so the
                // arithmetic is on the OFFSET rather than on a date. Shifting the day is what makes
                // a unique date field possible at all; returning null here ended the entity at its
                // second row.
                var text = value.GetValue<string>();
                var token = ClockToken().Match(text);
                if (!token.Success) return null;

                var days = int.Parse(token.Groups["days"].Value, CultureInfo.InvariantCulture);
                return Walk(Attempts, bump =>
                    text[..token.Index] + "{T" + Signed(days + bump) + "}" + text[(token.Index + token.Length)..],
                    used);
            }

            default:
                return Walk(Attempts, bump => $"{value.GetValue<string>()} {bump}", used);
        }
    }

    /// <summary>Candidate after candidate until one is free, or null when the space runs out.</summary>
    private static JsonNode? Walk<T>(int attempts, Func<int, T> candidate, HashSet<string> used)
    {
        for (var bump = 1; bump <= attempts; bump++)
        {
            JsonNode next = JsonValue.Create(candidate(bump))!;
            if (used.Add(next.ToJsonString())) return next;
        }
        return null;
    }

    [GeneratedRegex(@"\{T(?<days>[+-]?\d+)\}")]
    private static partial Regex ClockToken();

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
                // The DIRECTORY entity this points at, which is `targetEntity` in every case — a
                // `targetApp` says the record lives in the directory rather than in this
                // application, not that it is a person. Reading it as "person" seeded every company
                // and contact reference with the id of a human being, so a lead's Company resolved
                // to a name out of the staff list and nothing about the row looked wrong.
                var target = field.TargetEntity;
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

            // A PERSON's name, which the adjective-noun phrase cannot stand in for. `full_name` is
            // the platform's own spelling for it — core People and the directory's Contact both use
            // it — so this is a convention being honoured rather than a guess about a key. Drawn
            // from the same fixed cast as everything else, so a lead called "Priya Nair" is still
            // obviously demo data and not somebody who could be phoned.
            case "text" when field.Key == "full_name":
                // Walked by INDEX rather than hashed. A hash over twelve names puts the same person
                // on three of the first four rows often enough to look broken, and a list of leads
                // is exactly where somebody counts the distinct ones. Drawn from the EXTERNAL cast:
                // a lead or a customer contact is not one of the colleagues in `Names`.
                return ContactNames[index % ContactNames.Length];

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

    /// <summary>The customers, suppliers and partners a generated application deals with. A fixed
    /// cast, for the same reason the people are one: two screenshots of two applications should be
    /// recognisably the same demo.</summary>
    private static readonly string[] CompanyNames =
    [
        "Nordwind Logistik", "Kestrel Analytics", "Baumann Werke", "Lumen Health",
        "Sable & Finch", "Ostsee Marine", "Bаlmoral Foods", "Ardent Robotics",
    ];

    /// <summary>People OUTSIDE the company: a customer's contact, a lead, an applicant. Kept apart
    /// from <see cref="Names"/> so a generated demo never shows a colleague as a sales lead.</summary>
    private static readonly string[] ContactNames =
    [
        "Henrik Soltau", "Yara Benali", "Gregor Pfeiffer", "Nadia Osei",
        "Bertil Ahlgren", "Ines Vogel", "Callum Reid", "Mira Kovac",
        "Ana Ferreira", "Josef Klimt", "Ruth Nakamura", "Emeka Balogun",
        "Silje Haugen", "Theo Marchetti",
    ];

    private static readonly string[] Industries =
    [
        "Logistics", "Software", "Manufacturing", "Healthcare", "Retail", "Marine", "Food", "Robotics",
    ];

    private static readonly string[] JobTitles =
    [
        "Head of Operations", "CTO", "Procurement Lead", "Managing Director", "Finance Director",
    ];

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
