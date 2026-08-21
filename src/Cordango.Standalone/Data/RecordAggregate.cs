// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq.Expressions;
using System.Reflection;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Data;

/// <summary>One figure, or one bar of a chart.</summary>
/// <param name="Key">Null for an ungrouped total; otherwise the group this figure is for.</param>
/// <param name="Value">The number. Null when there was nothing to average.</param>
public sealed record AggregateBucket(string? Key, decimal? Value);

/// <summary>
/// Counting, summing and averaging rows — what a stat card and a chart both ask for.
///
/// <para><b>It happens in the database.</b> The obvious shortcut is to fetch the rows and add them
/// up in the browser, and it works beautifully until the table has a hundred thousand rows in it,
/// at which point the overview page downloads the whole business to display four numbers. Every
/// figure here is one <c>GROUP BY</c>.</para>
/// </summary>
public static class RecordAggregate
{
    /// <summary>Grouping by the month a date falls in, rather than by the date itself — written as
    /// <c>month_of:spent_on</c> in a definition, because "spend per month" is the question and
    /// "spend per day, then added up by whoever is reading" is not.</summary>
    private const string MonthPrefix = "month_of:";

    public static async Task<IReadOnlyList<AggregateBucket>> RunAsync<T>(
        IQueryable<T> query,
        RecordDescriptor<T> descriptor,
        string operation,
        string? field,
        string? groupBy,
        CancellationToken ct)
        where T : class, IRecord, new()
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (operation is not ("count" or "sum" or "avg" or "min" or "max"))
            throw new RecordException("aggregate.operation_unknown",
                $"'{operation}' is not an aggregate. Use count, sum, avg, min or max.");

        if (operation != "count" && string.IsNullOrEmpty(field))
            throw new RecordException("aggregate.field_required", $"'{operation}' needs a field to work on.");

        // Everything is projected to decimal? first, so one code path serves integers, decimals and
        // money instead of three near-identical ones differing only in the numeric type.
        var value = operation == "count" ? null : Numeric(descriptor, field!);

        if (string.IsNullOrEmpty(groupBy))
        {
            var total = await Single(query, operation, value, ct);
            return [new AggregateBucket(null, total)];
        }

        return await Grouped(query, descriptor, operation, value, groupBy, ct);
    }

    private static async Task<decimal?> Single<T>(
        IQueryable<T> query, string operation, Expression<Func<T, decimal?>>? value, CancellationToken ct)
    {
        if (operation == "count") return await query.CountAsync(ct);

        var numbers = query.Select(value!);
        return operation switch
        {
            "sum" => await numbers.SumAsync(ct),
            "avg" => await numbers.AverageAsync(ct),
            "min" => await numbers.MinAsync(ct),
            "max" => await numbers.MaxAsync(ct),
            _ => null,
        };
    }

    private static async Task<IReadOnlyList<AggregateBucket>> Grouped<T>(
        IQueryable<T> query,
        RecordDescriptor<T> descriptor,
        string operation,
        Expression<Func<T, decimal?>>? value,
        string groupBy,
        CancellationToken ct)
        where T : class, IRecord, new()
    {
        if (groupBy.StartsWith(MonthPrefix, StringComparison.Ordinal))
        {
            var key = MonthKey(descriptor, groupBy[MonthPrefix.Length..]);
            var buckets = await Group(query, key, operation, value, ct);

            // The database groups by a sortable number; the label is made here, where formatting is
            // cheap and does not have to be expressible in SQL.
            return [.. buckets
                .Where(b => b.Key is not null)
                .Select(b => new AggregateBucket(Month(b.Key!.Value), b.Value))
                .OrderBy(b => b.Key, StringComparer.Ordinal)];
        }

        var text = TextKey(descriptor, groupBy);
        var grouped = await Group(query, text, operation, value, ct);

        return [.. grouped
            .Select(b => new AggregateBucket(b.Key ?? "", b.Value))
            .OrderBy(b => b.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Group, and aggregate each group.
    ///
    /// <para><b>Two selectors into <c>GroupBy</c>, not a projection and then a group.</b> The first
    /// version projected each row to a small class and grouped by its key — which compiles, and
    /// which EF cannot translate: it inlines the projection into the key and gives up on
    /// <c>GroupBy(e =&gt; new Pair(…).Key)</c>. The error arrives at RUN time, on a real database,
    /// from a query that reads perfectly.</para>
    ///
    /// <para>EF's own <c>GroupBy(keySelector, elementSelector)</c> overload does the same job and
    /// translates directly, and it leaves each group as a plain sequence of numbers — so the
    /// aggregate is <c>g.Sum()</c> with no selector to splice in, and every lambda below is written
    /// out literally.</para>
    /// </summary>
    private static async Task<List<(TKey? Key, decimal? Value)>> Group<T, TKey>(
        IQueryable<T> query,
        Expression<Func<T, TKey?>> key,
        string operation,
        Expression<Func<T, decimal?>>? value,
        CancellationToken ct)
    {
        // Counting needs no field, and grouping still needs something to put in each group. Null per
        // row costs nothing and keeps one code path.
        value ??= _ => null;

        var grouped = query.GroupBy(key, value);

        var rows = operation switch
        {
            "count" => await grouped.Select(g => new { g.Key, Value = (decimal?)g.Count() }).ToListAsync(ct),
            "sum" => await grouped.Select(g => new { g.Key, Value = g.Sum() }).ToListAsync(ct),
            "avg" => await grouped.Select(g => new { g.Key, Value = g.Average() }).ToListAsync(ct),
            "min" => await grouped.Select(g => new { g.Key, Value = g.Min() }).ToListAsync(ct),
            "max" => await grouped.Select(g => new { g.Key, Value = g.Max() }).ToListAsync(ct),
            _ => [],
        };

        return [.. rows.Select(r => (r.Key, r.Value))];
    }

    /// <summary>The field to add up, as a nullable decimal. Nullable because a row that never had a
    /// value must not contribute a zero — an average over ten rows where three are blank is an
    /// average of seven.</summary>
    private static Expression<Func<T, decimal?>> Numeric<T>(RecordDescriptor<T> descriptor, string fieldKey)
        where T : class, IRecord, new()
    {
        var property = Property(descriptor, fieldKey);
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type != typeof(decimal) && type != typeof(long) && type != typeof(int) && type != typeof(double))
            throw new RecordException("aggregate.field_type",
                $"'{fieldKey}' is not a number, so it cannot be summed or averaged.");

        var parameter = Expression.Parameter(typeof(T), "e");
        Expression member = Expression.Property(parameter, property);
        return Expression.Lambda<Func<T, decimal?>>(Expression.Convert(member, typeof(decimal?)), parameter);
    }

    /// <summary>The group key as text — a status, a category, a reference id.</summary>
    private static Expression<Func<T, string?>> TextKey<T>(RecordDescriptor<T> descriptor, string fieldKey)
        where T : class, IRecord, new()
    {
        var property = Property(descriptor, fieldKey);
        var parameter = Expression.Parameter(typeof(T), "e");
        Expression member = Expression.Property(parameter, property);

        if (property.PropertyType != typeof(string))
        {
            // ToString() on the column: the database can do it for the types that reach here, and
            // the alternative is a separate grouped query per key type.
            var toString = property.PropertyType.GetMethod(nameof(ToString), Type.EmptyTypes)!;
            member = Expression.Call(member, toString);
        }

        return Expression.Lambda<Func<T, string?>>(member, parameter);
    }

    /// <summary>The month as one sortable number, <c>YYYYMM</c>. One integer rather than a pair,
    /// because a grouped query returning an anonymous key is harder to read back generically than
    /// one returning a number.</summary>
    private static Expression<Func<T, int?>> MonthKey<T>(RecordDescriptor<T> descriptor, string fieldKey)
        where T : class, IRecord, new()
    {
        var property = Property(descriptor, fieldKey);
        var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (underlying != typeof(DateOnly) && underlying != typeof(DateTimeOffset))
            throw new RecordException("aggregate.group_type",
                $"'{fieldKey}' is not a date, so records cannot be grouped by its month.");

        var parameter = Expression.Parameter(typeof(T), "e");
        Expression member = Expression.Property(parameter, property);

        var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;
        Expression date = nullable ? Expression.Property(member, "Value") : member;

        var year = Expression.Property(date, "Year");
        var month = Expression.Property(date, "Month");
        Expression key = Expression.Add(Expression.Multiply(year, Expression.Constant(100)), month);

        // A row with no date belongs to no month. Represented as null rather than dropped in the
        // query, so the caller can see how many there were if it wants to.
        Expression result = nullable
            ? Expression.Condition(
                Expression.Equal(member, Expression.Constant(null, property.PropertyType)),
                Expression.Constant(null, typeof(int?)),
                Expression.Convert(key, typeof(int?)))
            : Expression.Convert(key, typeof(int?));

        return Expression.Lambda<Func<T, int?>>(result, parameter);
    }

    private static string Month(int key) => $"{key / 100:D4}-{key % 100:D2}";

    private static PropertyInfo Property<T>(RecordDescriptor<T> descriptor, string fieldKey)
        where T : class, IRecord, new()
    {
        if (!descriptor.TryGetField(fieldKey, out var field))
            throw new RecordException("aggregate.field_unknown",
                $"'{descriptor.EntityKey}' has no field '{fieldKey}'.");

        return typeof(T).GetProperty(field.PropertyName)
            ?? throw new RecordException("aggregate.field_unknown",
                $"'{descriptor.EntityKey}.{fieldKey}' is declared but has no property to read.");
    }
}
