// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Data;

/// <summary>One condition a list is narrowed by.</summary>
/// <param name="Field">The definition's field key.</param>
/// <param name="Operator">One of the operators <see cref="RecordQuery"/> understands.</param>
/// <param name="Value">The comparison value, still as text — typed against the property it is
/// compared with, because only the property knows what "3" means.</param>
public sealed record RecordFilter(string Field, string Operator, string? Value);

/// <summary>Which field to order by, and which way.</summary>
public sealed record RecordSort(string Field, bool Descending);

/// <summary>
/// Turning a list request into a query the database runs.
///
/// <para><b>Why this is in the runtime and not generated.</b> Emitting a typed comparison per field
/// per operator would be sixteen field types times ten operators of near-identical code in every
/// application, and every one of them would have to be regenerated to fix a bug in any of them.
/// Building the expression once, from the property name the descriptor already carries, is the same
/// work done in one place — and it happens once per REQUEST, not once per row, so what runs against
/// the database is an ordinary parameterised query.</para>
///
/// <para><b>Fields are looked up, never interpolated.</b> A field key that is not in the descriptor
/// is refused by name rather than passed to anything. There is no path from a query string to a
/// column name here, which is what makes the difference between a filter and an injection.</para>
/// </summary>
public static class RecordQuery
{
    /// <summary>Everything a list request can ask for.</summary>
    public static IQueryable<T> Apply<T>(
        IQueryable<T> query,
        RecordDescriptor<T> descriptor,
        IReadOnlyList<RecordFilter> filters,
        IReadOnlyList<RecordSort> sort)
        where T : class, IRecord, new()
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(descriptor);

        foreach (var filter in filters ?? []) query = Where(query, descriptor, filter);
        return OrderBy(query, descriptor, sort ?? []);
    }

    /// <summary>Narrow, without ordering. An aggregate groups rather than pages, so the total order
    /// a list needs is only noise in the query tree.</summary>
    public static IQueryable<T> Narrow<T>(
        IQueryable<T> query, RecordDescriptor<T> descriptor, IReadOnlyList<RecordFilter> filters)
        where T : class, IRecord, new()
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(descriptor);

        foreach (var filter in filters ?? []) query = Where(query, descriptor, filter);
        return query;
    }

    private static IQueryable<T> Where<T>(IQueryable<T> query, RecordDescriptor<T> descriptor, RecordFilter filter)
        where T : class, IRecord, new()
    {
        var property = Property<T>(descriptor, filter.Field);
        var parameter = Expression.Parameter(typeof(T), "e");
        var member = Expression.Property(parameter, property);

        var body = Compare(member, property.PropertyType, filter);
        return query.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    private static Expression Compare(MemberExpression member, Type type, RecordFilter filter)
    {
        // The absence tests come first: they are the two operators that do not need a value, and
        // treating a missing value as an error before checking for them would make them unusable.
        switch (filter.Operator)
        {
            case "isEmpty":
                return IsEmpty(member, type);
            case "isNotEmpty":
                return Expression.Not(IsEmpty(member, type));
        }

        if (filter.Operator is "in" or "notIn")
        {
            var values = (filter.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => Constant(v, type))
                .ToList();

            if (values.Count == 0)
            {
                // "in nothing" matches nothing, and "not in nothing" matches everything. Falling
                // through to an OR over an empty list would silently do the opposite of both.
                return Expression.Constant(filter.Operator == "notIn");
            }

            Expression any = Expression.Equal(member, values[0]);
            for (var i = 1; i < values.Count; i++)
                any = Expression.OrElse(any, Expression.Equal(member, values[i]));

            return filter.Operator == "in" ? any : Expression.Not(any);
        }

        if (filter.Operator is "contains" or "startsWith")
        {
            if (type != typeof(string))
                throw new RecordException("query.operator_type",
                    $"'{filter.Operator}' compares text, and '{filter.Field}' is not text.");

            var method = filter.Operator == "contains"
                ? typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!
                : typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

            // A null column is not a match, and calling Contains on it would throw once the query
            // ran rather than when it was built.
            return Expression.AndAlso(
                Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                Expression.Call(member, method, Expression.Constant(filter.Value ?? "", typeof(string))));
        }

        var constant = Constant(filter.Value, type);
        return filter.Operator switch
        {
            "eq" => Expression.Equal(member, constant),
            "neq" => Expression.NotEqual(member, constant),
            "gt" => Expression.GreaterThan(member, constant),
            "gte" => Expression.GreaterThanOrEqual(member, constant),
            "lt" => Expression.LessThan(member, constant),
            "lte" => Expression.LessThanOrEqual(member, constant),
            _ => throw new RecordException("query.operator_unknown",
                $"'{filter.Operator}' is not a filter operator. Use eq, neq, gt, gte, lt, lte, in, notIn, contains, startsWith, isEmpty or isNotEmpty."),
        };
    }

    /// <summary>Empty means null, and for text it also means the empty string — a field somebody
    /// cleared in a form and a field nobody ever filled are the same thing to the person asking.</summary>
    private static Expression IsEmpty(MemberExpression member, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

        Expression test = nullable
            ? Expression.Equal(member, Expression.Constant(null, type))
            : Expression.Constant(false);

        if (underlying == typeof(string))
            test = Expression.OrElse(test, Expression.Equal(member, Expression.Constant("", typeof(string))));

        return test;
    }

    /// <summary>
    /// The comparison value, typed against the column.
    ///
    /// <para>Wrapped as a closure over a variable rather than a literal, so EF parameterises it
    /// instead of inlining it into the SQL — which is both faster, because the plan is reusable, and
    /// the reason none of this can be injected into.</para>
    /// </summary>
    private static Expression Constant(string? raw, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        object? value;

        if (raw is null)
        {
            value = null;
        }
        else if (underlying == typeof(string)) value = raw;
        else if (underlying == typeof(bool)) value = raw is "true" or "1" or "yes";
        else if (underlying == typeof(long)) value = ParseOrThrow<long>(raw, long.TryParse);
        else if (underlying == typeof(int)) value = ParseOrThrow<int>(raw, int.TryParse);
        else if (underlying == typeof(decimal)) value = ParseOrThrow<decimal>(raw, decimal.TryParse);
        else if (underlying == typeof(double)) value = ParseOrThrow<double>(raw, double.TryParse);
        else if (underlying == typeof(DateOnly)) value = ParseOrThrow<DateOnly>(raw, DateOnly.TryParse);
        else if (underlying == typeof(DateTimeOffset)) value = ParseOrThrow<DateTimeOffset>(raw, DateTimeOffset.TryParse);
        else if (underlying == typeof(Guid)) value = ParseOrThrow<Guid>(raw, Guid.TryParse);
        else value = raw;

        // A closure, so this arrives as a parameter. Expression.Constant would be baked into the SQL.
        var box = Expression.Constant(new Box(value));
        return Expression.Convert(Expression.Field(box, nameof(Box.Value)), type);
    }

    private delegate bool TryParse<TValue>(string text, IFormatProvider? provider, out TValue value);

    private static object ParseOrThrow<TValue>(string raw, TryParse<TValue> parse)
    {
        if (parse(raw, CultureInfo.InvariantCulture, out var value)) return value!;
        throw new RecordException("query.value_invalid", $"'{raw}' is not a valid {typeof(TValue).Name}.");
    }

    private sealed class Box(object? value)
    {
        public readonly object? Value = value;
    }

    private static IQueryable<T> OrderBy<T>(IQueryable<T> query, RecordDescriptor<T> descriptor, IReadOnlyList<RecordSort> sort)
        where T : class, IRecord, new()
    {
        // Ordered by id when nothing else says otherwise, and id is appended even when something
        // does. A page is only meaningful over a total order: sorting by a status that half the
        // rows share leaves the database free to return those rows in a different sequence on each
        // page, so a row can appear twice and another never.
        IOrderedQueryable<T>? ordered = null;

        foreach (var term in sort)
        {
            var property = Property<T>(descriptor, term.Field);
            ordered = Order(ordered ?? (IOrderedQueryable<T>?)null, query, property, term.Descending, ordered is not null);
        }

        return ordered is null
            ? query.OrderBy(e => e.Id)
            : ordered.ThenBy(e => e.Id);
    }

    private static IOrderedQueryable<T> Order<T>(
        IOrderedQueryable<T>? ordered, IQueryable<T> query, PropertyInfo property, bool descending, bool then)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        var member = Expression.Property(parameter, property);
        var selector = Expression.Lambda(member, parameter);

        var name = (then, descending) switch
        {
            (false, false) => nameof(Queryable.OrderBy),
            (false, true) => nameof(Queryable.OrderByDescending),
            (true, false) => nameof(Queryable.ThenBy),
            (true, true) => nameof(Queryable.ThenByDescending),
        };

        var method = typeof(Queryable).GetMethods()
            .First(m => m.Name == name && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(T), property.PropertyType);

        var source = then ? (object)ordered! : query;
        return (IOrderedQueryable<T>)method.Invoke(null, [source, selector])!;
    }

    /// <summary>
    /// The property behind a field key — and a refusal by name when there is none.
    ///
    /// <para>The descriptor is the allowlist. Nothing from a query string reaches
    /// <see cref="Type.GetProperty(string)"/> without passing through it first, so a request naming
    /// a field this entity does not have is answered with a message rather than an exception from
    /// somewhere deeper.</para>
    /// </summary>
    private static PropertyInfo Property<T>(RecordDescriptor<T> descriptor, string fieldKey)
        where T : class, IRecord, new()
    {
        if (!descriptor.TryGetField(fieldKey, out var field))
            throw new RecordException("query.field_unknown",
                $"'{descriptor.EntityKey}' has no field '{fieldKey}'.");

        return typeof(T).GetProperty(field.PropertyName)
            ?? throw new RecordException("query.field_unknown",
                $"'{descriptor.EntityKey}.{fieldKey}' is declared but has no property to read.");
    }

    /// <summary>
    /// Parse the query string's filter terms: <c>field:operator:value</c>.
    ///
    /// <para>Split into three at most, so a value containing a colon — a time, a URL — survives
    /// intact.</para>
    /// </summary>
    public static IReadOnlyList<RecordFilter> ParseFilters(IEnumerable<string?>? terms)
    {
        if (terms is null) return [];

        var filters = new List<RecordFilter>();
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;

            var parts = term.Split(':', 3);
            filters.Add(parts.Length switch
            {
                >= 3 => new RecordFilter(parts[0], parts[1], parts[2]),
                2 => new RecordFilter(parts[0], parts[1], null),
                _ => throw new RecordException("query.filter_invalid",
                    $"'{term}' is not a filter. Write field:operator:value, for example status:eq:open."),
            });
        }

        return filters;
    }

    /// <summary>Parse the sort terms: <c>field</c> ascending, <c>-field</c> descending.</summary>
    public static IReadOnlyList<RecordSort> ParseSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort)) return [];

        return [.. sort
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.StartsWith('-')
                ? new RecordSort(term[1..], true)
                : new RecordSort(term, false))];
    }
}
