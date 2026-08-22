// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using Cordango.Definition;

namespace Cordango.Standalone.Tests;

public class GeneratedArithmeticTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    [Theory]
    [InlineData(1L, 3L, 33)]
    [InlineData(2L, 4L, 50)]
    [InlineData(7L, 7L, 100)]
    [InlineData(0L, 5L, 0)]
    [InlineData(3L, 0L, null)]
    [InlineData(null, null, null)]
    public async Task Generated_progress_matches_the_evaluator(long? done, long? total, int? expected)
    {
        if (Skipped) return;

        var computed = await Built();

        var record = Activator.CreateInstance(computed.Entity)!;
        Set(record, "DoneTasks", done);
        Set(record, "TotalTasks", total);

        var actual = (decimal?)computed.Progress.Invoke(null, [record]);

        // The expected values are stated here rather than compared against an evaluator, because
        // there is no longer an evaluator in this repository to compare against: working a figure out
        // over a record is the platform's job and its code moved there with it. What replaced that
        // comparison is `GeneratedComputedTests`, which drives the whole of
        // `tests/fixtures/computed/` through generated code — the same hand-written cases the
        // platform's own suite holds its evaluator to. This test keeps its own value by exercising a
        // REAL corpus application's integer columns rather than a synthetic one.
        Assert.Equal(expected, actual is null ? null : (int?)decimal.Truncate(actual.Value));
    }

    [Fact]
    public async Task A_computed_method_works_in_decimal_whatever_the_column_holds()
    {
        if (Skipped) return;

        var computed = await Built();
        Assert.Equal(typeof(decimal?), computed.Progress.ReturnType);
    }

    private static async Task<(Type Entity, MethodInfo Progress)> Built()
    {
        await Gate.WaitAsync();
        try
        {
            if (_built is not null) return _built.Value;

            var app = GeneratedApplicationTests.Materialise("task-manager");
            await GeneratedApplicationTests.Build(app);

            var assembly = Assembly.LoadFrom(Path.Combine(
                app.Root, "api", "bin", "Release", "net10.0", "TaskManager.Api.dll"));

            var entity = assembly.GetType("TaskManager.Entities.Project", throwOnError: true)!;
            var computed = assembly.GetType("TaskManager.Computed.ProjectComputed", throwOnError: true)!;

            _built = (entity, computed.GetMethod("Progress", BindingFlags.Public | BindingFlags.Static)!);
            return _built.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Set(object record, string property, long? value) =>
        record.GetType().GetProperty(property)!.SetValue(record, value);

    private static (Type Entity, MethodInfo Progress)? _built;

    private static readonly SemaphoreSlim Gate = new(1, 1);
}
