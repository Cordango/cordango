// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using Cordango.Standalone.Workflows;

namespace Cordango.Standalone.Tests;

public class CronScheduleTests
{
    [Theory]
    [InlineData("* * * * *", "2026-03-15T08:00:00Z", true)]
    [InlineData("* * * * *", "2026-03-15T13:47:00Z", true)]
    [InlineData("0 8 * * *", "2026-03-15T08:00:00Z", true)]
    [InlineData("0 8 * * *", "2026-03-15T08:01:00Z", false)]
    [InlineData("0 8 * * *", "2026-03-15T09:00:00Z", false)]
    [InlineData("0 9,17 * * *", "2026-03-15T17:00:00Z", true)]
    [InlineData("0 9,17 * * *", "2026-03-15T12:00:00Z", false)]
    [InlineData("30 9-17 * * *", "2026-03-15T14:30:00Z", true)]
    [InlineData("30 9-17 * * *", "2026-03-15T18:30:00Z", false)]
    [InlineData("*/15 * * * *", "2026-03-15T10:30:00Z", true)]
    [InlineData("*/15 * * * *", "2026-03-15T10:31:00Z", false)]
    [InlineData("0 8 1 * *", "2026-03-01T08:00:00Z", true)]
    [InlineData("0 8 1 * *", "2026-03-15T08:00:00Z", false)]
    [InlineData("0 8 * * 0", "2026-03-15T08:00:00Z", true)]
    [InlineData("0 8 * * 1", "2026-03-15T08:00:00Z", false)]
    public void Minutes_match_or_do_not(string expression, string instant, bool expected) =>
        Assert.Equal(expected, CronSchedule.Matches(expression, Parse(instant)));

    [Theory]
    [InlineData("2026-03-01T08:00:00Z", true)]
    [InlineData("2026-03-02T08:00:00Z", true)]
    [InlineData("2026-03-03T08:00:00Z", false)]
    public void Day_of_month_and_day_of_week_are_ored(string instant, bool expected) =>
        Assert.Equal(expected, CronSchedule.Matches("0 8 1 * 1", Parse(instant)));

    [Theory]
    [InlineData("")]
    [InlineData("0 8 * *")]
    [InlineData("0 8 * * * *")]
    [InlineData("every morning")]
    [InlineData("0 99 * * *")]
    [InlineData("0 8 * * 9")]
    [InlineData("*/0 * * * *")]
    public void An_unreadable_expression_never_matches(string expression)
    {
        Assert.False(CronSchedule.IsValid(expression));

        var start = Parse("2026-03-15T00:00:00Z");
        for (var minute = 0; minute < 24 * 60; minute++)
            Assert.False(CronSchedule.Matches(expression, start.AddMinutes(minute)));
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 8 * * *")]
    [InlineData("30 9-17 1,15 * 1-5")]
    [InlineData("*/15 */2 * * *")]
    public void A_readable_expression_is_readable(string expression) =>
        Assert.True(CronSchedule.IsValid(expression));

    private static DateTimeOffset Parse(string instant) =>
        DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
}
