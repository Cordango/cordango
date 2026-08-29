// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.DotNetVue.Model;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Tests;

public class DatePartTests
{
    private static readonly DateOnly Sunday = new(2026, 3, 1);
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Saturday = new(2026, 3, 7);

    [Fact]
    public void A_monday_week_numbers_monday_first()
    {
        Assert.Equal(1m, Calc.Weekday(Monday, weekStartsMonday: true));
        Assert.Equal(7m, Calc.Weekday(Sunday, weekStartsMonday: true));
        Assert.Equal(6m, Calc.Weekday(Saturday, weekStartsMonday: true));
    }

    [Fact]
    public void A_sunday_week_numbers_sunday_first()
    {
        Assert.Equal(1m, Calc.Weekday(Sunday, weekStartsMonday: false));
        Assert.Equal(2m, Calc.Weekday(Monday, weekStartsMonday: false));
        Assert.Equal(7m, Calc.Weekday(Saturday, weekStartsMonday: false));
    }

    [Fact]
    public void The_convention_moves_the_boundary_not_just_the_numbering()
    {
        Assert.NotEqual(
            Calc.WeekOfYear(Sunday, weekStartsMonday: true),
            Calc.WeekOfYear(Monday, weekStartsMonday: true));

        Assert.Equal(
            Calc.WeekOfYear(Sunday, weekStartsMonday: false),
            Calc.WeekOfYear(Monday, weekStartsMonday: false));
    }

    [Fact]
    public void The_first_of_january_is_always_week_one()
    {
        foreach (var year in new[] { 2024, 2025, 2026, 2027, 2028 })
            foreach (var monday in new[] { true, false })
                Assert.Equal(1m, Calc.WeekOfYear(new DateOnly(year, 1, 1), monday));
    }

    [Fact]
    public void No_year_runs_past_fifty_four_weeks()
    {
        foreach (var year in new[] { 2024, 2025, 2026, 2027, 2028 })
            foreach (var monday in new[] { true, false })
            {
                var last = Calc.WeekOfYear(new DateOnly(year, 12, 31), monday);
                Assert.NotNull(last);
                Assert.InRange(last!.Value, 52m, 54m);
            }
    }

    [Fact]
    public void A_week_number_never_goes_backwards_inside_a_year()
    {
        foreach (var monday in new[] { true, false })
        {
            var previous = 0m;
            for (var day = new DateOnly(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
            {
                var week = Calc.WeekOfYear(day, monday)!.Value;
                Assert.True(week >= previous, $"{day:O} went back to week {week} from {previous}");
                previous = week;
            }
        }
    }

    [Fact]
    public void A_blank_date_has_no_parts()
    {
        Assert.Null(Calc.Weekday((DateOnly?)null, weekStartsMonday: true));
        Assert.Null(Calc.WeekOfYear((DateOnly?)null, weekStartsMonday: true));
        Assert.Null(Calc.MonthOf((DateOnly?)null));
        Assert.Null(Calc.DayOfMonth((DateOnly?)null));
        Assert.Null(Calc.DayOfYear((DateOnly?)null));
        Assert.Null(Calc.YearOf((DateOnly?)null));
        Assert.Null(Calc.HourOf(null));
    }

    [Fact]
    public void A_stored_instant_is_read_in_the_zone_it_was_stored_in()
    {
        var lateInBerlin = new DateTimeOffset(2026, 3, 3, 0, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(22m, Calc.HourOf(lateInBerlin));
        Assert.Equal(1m, Calc.Weekday(lateInBerlin, weekStartsMonday: true));
        Assert.Equal(2m, Calc.DayOfMonth(lateInBerlin));
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData("monday", "true")]
    [InlineData("sunday", "false")]
    public void The_apps_convention_is_written_into_the_generated_call(string? weekStart, string expected)
    {
        var (app, entity) = Application(weekStart);

        var written = ComputedEmitter.Expression(app, entity, Computed("weekday(shift_date)"));

        Assert.NotNull(written);
        Assert.Contains($"Calc.Weekday(r.ShiftDate, {expected})", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_does_not_depend_on_the_week_is_written_without_it()
    {
        var (app, entity) = Application("sunday");

        var written = ComputedEmitter.Expression(app, entity, Computed("month_of(shift_date)"));

        Assert.NotNull(written);
        Assert.Contains("Calc.MonthOf(r.ShiftDate)", written, StringComparison.Ordinal);
    }

    private static FieldModel Computed(string expr) => new(new JsonObject
    {
        ["key"] = "the_part",
        ["label"] = "Part",
        ["type"] = "decimal",
        ["computed"] = new JsonObject { ["expr"] = expr },
    }, "shift");

    private static (AppModel App, EntityModel Entity) Application(string? weekStart)
    {
        var entity = new JsonObject
        {
            ["key"] = "shift",
            ["label"] = "Shift",
            ["fields"] = new JsonArray(
                new JsonObject { ["key"] = "shift_date", ["label"] = "Shift date", ["type"] = "date" },
                new JsonObject { ["key"] = "the_part", ["label"] = "Part", ["type"] = "decimal" }),
        };

        var manifest = new JsonObject
        {
            ["key"] = "roster",
            ["name"] = "Roster",
            ["entities"] = new JsonArray(entity.DeepClone()),
        };

        if (weekStart is not null) manifest["weekStart"] = weekStart;

        var app = AppModel.From(new CompiledAppArtifact(
            manifest, manifest, "unhashed", new CompilerInfo("test", "1")));

        return (app, new EntityModel(entity, "Roster"));
    }
}
