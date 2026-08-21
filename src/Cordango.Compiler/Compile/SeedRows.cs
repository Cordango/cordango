// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compile;

/// <summary>The row mechanics of a seed run, kept pure (no AI, no I/O, no DB) so they can be tested:
/// building a grid's cross product, resolving the date window it spans, and merging the model's answer
/// back onto the rows. <see cref="AppBuilder.Api.Services.AppSeedService"/> supplies the data.</summary>
public static class SeedRows
{
    /// <summary>The cross product of a grid's axes, each row carrying its own axis keys — this is what
    /// makes a composed grid render FULL. Left to choose its own rows the model emits a scattering, and a
    /// matrix with 4 of 60 cells filled reads as broken rather than sparse.
    /// An axis with an empty pool yields nothing: a cell that cannot be keyed cannot be seeded.</summary>
    public static List<JsonObject> Grid(GridJoin grid,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pools, DateTime today, int max)
    {
        var rows = new List<JsonObject> { new() };
        foreach (var axis in grid.AxisFields)
        {
            if (!pools.TryGetValue(axis, out var pool) || pool.Count == 0) return [];
            rows = rows.SelectMany(r => pool.Select(id => With(r, axis, id))).ToList();
        }
        if (grid.Dates is { } d)
            rows = rows.SelectMany(r => DateWindow(d, today).Select(day => With(r, d.Field, day))).ToList();
        return rows.Count == 1 && rows[0].Count == 0 ? [] : rows.Take(max).ToList();
    }

    /// <summary>The dates a grid's column axis actually shows. `from` is normally the token `{{today}}`,
    /// which the renderer resolves at render time — so the seeder must resolve it the SAME way, or rows
    /// land outside the window and the grid renders empty. That is precisely why the generated planner
    /// came out blank: its shifts were seeded across ±45 days while the roster looked at 7.</summary>
    public static List<string> DateWindow(DateAxis d, DateTime today)
    {
        var from = DateTime.TryParse(d.From, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.Date : today.Date;
        return Enumerable.Range(0, Math.Clamp(d.Count, 1, 60)).Select(i => (d.Step switch
        {
            "week" => from.AddDays(i * 7),
            "month" => from.AddMonths(i),
            _ => from.AddDays(i),
        }).ToString("yyyy-MM-dd")).ToList();
    }

    /// <summary>Merge the model's records onto <paramref name="rows"/>, turning every reference INDEX back
    /// into an id. The model picks references by index into the pool it was shown — an index costs a
    /// couple of tokens where a GUID costs ~15, and an out-of-range one is simply dropped rather than
    /// becoming a dangling reference.
    ///
    /// Grid rows are matched by the `row` number the model echoes back, so a partial or reordered answer
    /// still lands on the right cells; plain rows are matched positionally. Axis keys already on a row are
    /// never overwritten — those are ours.
    ///
    /// Returns the rows the model actually filled. A grid's cross product ENUMERATES every cell so the
    /// model is asked to cover them all, but only the ones it answered are worth inserting: forcing every
    /// cell would put every employee on every shift of the week and leave un-answered cells as records
    /// carrying nothing but their axis keys. The model's own choices are where a roster gets days off.</summary>
    public static List<JsonObject> Apply(List<JsonObject> rows, JsonArray filled,
        IReadOnlyDictionary<string, IReadOnlyList<string>> refPools, bool byRowIndex)
    {
        var touched = new HashSet<int>();
        for (var i = 0; i < filled.Count; i++)
        {
            if (filled[i] is not JsonObject src) continue;

            int index;
            if (byRowIndex)
            {
                if (Int(src["row"]) is not { } r || r < 0 || r >= rows.Count) continue;
                index = r;
            }
            else
            {
                if (i >= rows.Count) continue;
                index = i;
            }
            var target = rows[index];

            foreach (var (k, v) in src)
            {
                if (k == "row" || v is null || target.ContainsKey(k)) continue;
                if (refPools.TryGetValue(k, out var pool))
                {
                    if (Int(v) is { } idx && idx >= 0 && idx < pool.Count) target[k] = pool[idx];
                    continue;   // out of range → leave unset rather than point at nothing
                }
                target[k] = v.DeepClone();
            }
            touched.Add(index);
        }
        return touched.OrderBy(i => i).Select(i => rows[i]).ToList();
    }

    private static JsonObject With(JsonObject row, string key, string value)
    {
        var n = row.DeepClone().AsObject();
        n[key] = value;
        return n;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
