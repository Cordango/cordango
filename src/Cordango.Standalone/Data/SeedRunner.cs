// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cordango.Standalone.Data;

/// <summary>One entity the seed file can fill. Registered for every entity, generated or not.</summary>
public interface ISeedTarget
{
    string Entity { get; }

    Task<int> ApplyAsync(JsonArray rows, DateOnly anchor, CancellationToken ct);
}

/// <summary>
/// Inserting one entity's seed rows.
///
/// <para>Rows go in through the ordinary <see cref="DbContext"/> rather than through the store, so
/// hooks do NOT fire. Seeding is a bulk load of a dataset that was already computed, and letting a
/// workflow react to two hundred inserts would send two hundred notifications about records nobody
/// created.</para>
/// </summary>
public sealed class SeedTarget<T> : ISeedTarget where T : class, IRecord, new()
{
    private readonly CordangoDbContext _db;
    private readonly RecordDescriptor<T> _descriptor;

    public SeedTarget(CordangoDbContext db, RecordDescriptor<T> descriptor)
    {
        _db = db;
        _descriptor = descriptor;
    }

    public string Entity => _descriptor.EntityKey;

    public async Task<int> ApplyAsync(JsonArray rows, DateOnly anchor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Only ever into an empty table. A seed run over live data would either duplicate the
        // dataset or overwrite somebody's work, and neither is what "load the demo data" means.
        if (await _db.Set<T>().AnyAsync(ct)) return 0;

        var added = 0;
        foreach (var row in rows.OfType<JsonObject>())
        {
            var resolved = SeedRunner.Resolve(row, anchor);
            var record = resolved.Deserialize<T>(SeedRunner.Json);
            if (record is null) continue;

            _db.Set<T>().Add(record);
            added++;
        }

        await _db.SaveChangesAsync(ct);
        return added;
    }
}

/// <summary>
/// Loading the dataset a build produced.
///
/// <para><b>The same seed file always produces the same rows.</b> Dates are stored as offsets from
/// an anchor the generator recorded — <c>{T-14}</c> is fourteen days before it — so a dataset built
/// in March still reads sensibly, with the same shape, whenever it is loaded. Setting
/// <c>Seed:Date</c> to <c>today</c> re-anchors on the day it runs, which makes the data look current
/// and makes the run non-reproducible; that is a deliberate choice with a name rather than a
/// default.</para>
/// </summary>
public static class SeedRunner
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Load and apply, if there is a file and the application asked for it.</summary>
    public static async Task RunAsync(
        IServiceProvider services, string path, string? dateMode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");

        if (!File.Exists(path))
        {
            log.LogInformation("No seed file at {Path}; nothing to load.", path);
            return;
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject();
        if (document is null)
        {
            log.LogWarning("The seed file at {Path} could not be read.", path);
            return;
        }

        var recorded = document["anchor"]?.GetValue<string>();
        var anchor = string.Equals(dateMode, "today", StringComparison.OrdinalIgnoreCase)
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : DateOnly.TryParse(recorded, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.UtcNow);

        var targets = services.GetServices<ISeedTarget>().ToDictionary(t => t.Entity, StringComparer.Ordinal);
        var total = 0;

        // In file order, which is the order the generator worked out: an entity is written after
        // everything it points at, so a reference always has something to resolve against.
        foreach (var block in (document["entities"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var entity = block["entity"]?.GetValue<string>();
            if (entity is null) continue;

            if (!targets.TryGetValue(entity, out var target))
            {
                log.LogWarning("The seed file has rows for '{Entity}', which this application does not have.", entity);
                continue;
            }

            var added = await target.ApplyAsync(block["rows"] as JsonArray ?? [], anchor, ct);
            total += added;
            if (added > 0) log.LogInformation("Seeded {Count} {Entity} records.", added, entity);
        }

        log.LogInformation(total > 0
            ? $"Seeding complete: {total} records, anchored on {anchor:yyyy-MM-dd}."
            : "Nothing seeded — the tables already have rows in them.");
    }

    /// <summary>Replace the date offsets in one row. <c>{T}</c> is the anchor, <c>{T-14}</c> and
    /// <c>{T+3}</c> are days either side of it.</summary>
    internal static JsonObject Resolve(JsonObject row, DateOnly anchor)
    {
        var clone = (JsonObject)row.DeepClone();

        foreach (var (key, value) in clone.ToList())
        {
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text)) continue;

            var match = Offset.Match(text);
            if (!match.Success) continue;

            var days = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var date = anchor.AddDays(days);

            // A datetime column wants a full instant; a date column wants a day. The token is the
            // same either way, so the SHAPE of the surrounding text decides — "{T-3}T09:00:00Z"
            // keeps its time.
            clone[key] = text.Length == match.Length
                ? date.ToString("yyyy-MM-dd")
                : Offset.Replace(text, date.ToString("yyyy-MM-dd"));
        }

        return clone;
    }

    private static readonly Regex Offset = new(@"\{T([+-]\d+)?\}", RegexOptions.Compiled);
}
