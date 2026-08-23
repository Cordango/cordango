// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Tests;

public class RecordQueryTests
{
    private sealed class Task : IRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string? Title { get; set; }
        public DateOnly? DueOn { get; set; }
        public bool Done { get; set; }
    }

    private static readonly RecordDescriptor<Task> Descriptor = new("task", "Task",
    [
        new RecordField<Task>("id", nameof(Task.Id), (from, to) => to.Id = from.Id),
        new RecordField<Task>("title", nameof(Task.Title), (from, to) => to.Title = from.Title),
        new RecordField<Task>("due_on", nameof(Task.DueOn), (from, to) => to.DueOn = from.DueOn),
        new RecordField<Task>("done", nameof(Task.Done), (from, to) => to.Done = from.Done),
    ]);

    private static readonly Task Overdue = new() { Title = "overdue", DueOn = new DateOnly(2026, 1, 1) };
    private static readonly Task Soon = new() { Title = "soon", DueOn = new DateOnly(2026, 6, 15) };
    private static readonly Task Someday = new() { Title = "someday", DueOn = null };

    private static IReadOnlyList<string> Matching(params string[] terms) =>
    [
        .. RecordQuery
            .Apply(new[] { Overdue, Soon, Someday }.AsQueryable(), Descriptor,
                RecordQuery.ParseFilters(terms), [])
            .Select(t => t.Title!)
    ];

    [Theory]
    [InlineData("due_on:lt:2026-03-01", "overdue")]
    [InlineData("due_on:lte:2026-01-01", "overdue")]
    [InlineData("due_on:gt:2026-03-01", "soon")]
    [InlineData("due_on:gte:2026-06-15", "soon")]
    public void A_comparison_against_a_column_that_may_be_empty_is_a_query(string term, string expected)
    {
        Assert.Equal([expected], Matching(term));
    }

    [Fact]
    public void A_row_with_no_value_matches_no_comparison()
    {
        Assert.DoesNotContain("someday", Matching("due_on:lt:2030-01-01"));
        Assert.DoesNotContain("someday", Matching("due_on:gt:2000-01-01"));
    }

    [Fact]
    public void Between_takes_both_bounds_and_includes_them()
    {
        Assert.Equal(["overdue", "soon"], Matching("due_on:between:2026-01-01|2026-06-15"));
        Assert.Equal(["soon"], Matching("due_on:between:2026-02-01|2026-12-31"));
        Assert.Empty(Matching("due_on:between:2027-01-01|2027-12-31"));
    }

    [Fact]
    public void Between_without_two_bounds_says_so()
    {
        var failure = Assert.Throws<RecordException>(() => Matching("due_on:between:2026-01-01"));

        Assert.Equal("query.range_invalid", failure.Code);
    }

    [Fact]
    public void An_unknown_operator_names_the_ones_that_exist()
    {
        var failure = Assert.Throws<RecordException>(() => Matching("due_on:sometime:2026-01-01"));

        Assert.Equal("query.operator_unknown", failure.Code);
        Assert.Contains("between", failure.Message, StringComparison.Ordinal);
    }
}
