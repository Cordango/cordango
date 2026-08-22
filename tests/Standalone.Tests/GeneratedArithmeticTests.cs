// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using Cordango.Definition;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The generated arithmetic gives the SAME answers as the definition's own evaluator.
///
/// <para><b>Compiling is not computing.</b> Every other test here proves the generator produces C#
/// that builds; none of them would notice if it built and returned the wrong number. And a wrong
/// number is the worst failure this project can have — a total that is quietly different on the
/// platform and in a generated application, from one definition, discovered a quarter later by
/// somebody reconciling two reports.</para>
///
/// <para>So this builds a real application, loads it, and runs its computed methods against the same
/// inputs <see cref="ComputedExpr"/> evaluates. Two implementations, one set of answers — the same
/// arrangement the permission and condition fixtures use, except that one side has to be compiled
/// before it can be asked.</para>
///
/// <para>Each case is a rule that has already been got wrong once here: a blank reading as zero, a
/// division by zero reading as unknown rather than as zero, and two integer columns doing DECIMAL
/// arithmetic rather than truncating at every step.</para>
/// </summary>
public class GeneratedArithmeticTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    /// <summary><c>project.progress</c> is <c>done_tasks * 100 / total_tasks</c> over two integer
    /// columns — the smallest expression that exercises all three rules at once.</summary>
    [Theory]
    // The ordinary case. Integer columns, and 1 * 100 / 3 is 33.33…, not 33 — decimal arithmetic
    // throughout, narrowed once at the end.
    [InlineData(1L, 3L, 33)]
    [InlineData(2L, 4L, 50)]
    [InlineData(7L, 7L, 100)]
    // Nothing done yet is nought per cent, not blank: a blank NUMBER reads as zero.
    [InlineData(0L, 5L, 0)]
    // No tasks at all is not nought per cent — nobody knows what the progress is. A division by zero
    // is unknown, and a project showing 0% because it is empty would read as a project in trouble.
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

        // Against the definition's own evaluator, on the same inputs, rather than against a number
        // typed into this file. The InlineData is there to say what the answer SHOULD look like; the
        // evaluator is what says it is right.
        var reference = ComputedExpr.Evaluate(
            "done_tasks * 100 / total_tasks",
            key => key switch
            {
                "done_tasks" => done,
                "total_tasks" => total,
                _ => null,
            });

        Assert.Equal(reference, actual);
        Assert.Equal(expected, actual is null ? null : (int?)decimal.Truncate(actual.Value));
    }

    /// <summary>
    /// The generated method returns <c>decimal?</c> even though the column is an integer.
    ///
    /// <para>The narrowing happens once, in <c>Apply</c>, and not at every intermediate step.
    /// Rounding on the way through would make the answer depend on how the author happened to split
    /// the expression, which is not something a definition should be able to notice.</para>
    /// </summary>
    [Fact]
    public async Task A_computed_method_works_in_decimal_whatever_the_column_holds()
    {
        if (Skipped) return;

        var computed = await Built();
        Assert.Equal(typeof(decimal?), computed.Progress.ReturnType);
    }

    /// <summary>The application, built once and shared. Building it per case would be eight
    /// `dotnet build` runs for six assertions.</summary>
    private static async Task<(Type Entity, MethodInfo Progress)> Built()
    {
        await Gate.WaitAsync();
        try
        {
            if (_built is not null) return _built.Value;

            // NOT disposed. Loading an assembly holds its file open for the life of the process, so
            // deleting the directory underneath it fails — on Windows loudly, with an
            // UnauthorizedAccessException from the temp cleanup rather than from anything to do with
            // the test. The directory is left in TEMP and the operating system deals with it.
            var app = GeneratedApplicationTests.Materialise("task-manager");
            await GeneratedApplicationTests.Build(app);

            // LoadFrom rather than a collectible context: the assembly stays for the life of the
            // test run, which is fine, and the alternative is resolving every dependency by hand.
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
