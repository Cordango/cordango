// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using Cordango.Standalone.Workflows;

namespace Cordango.Standalone.Tests;

/// <summary>
/// When a scheduled workflow runs.
///
/// <para>Cron is worth getting exactly right because the failure is silent in both directions: an
/// expression that matches too often sends a reminder every minute, and one that matches never looks
/// identical to a scheduler that is not running.</para>
/// </summary>
public class CronScheduleTests
{
    [Theory]
    // Every minute.
    [InlineData("* * * * *", "2026-03-15T08:00:00Z", true)]
    [InlineData("* * * * *", "2026-03-15T13:47:00Z", true)]
    // Eight in the morning, every day — the shape crm uses.
    [InlineData("0 8 * * *", "2026-03-15T08:00:00Z", true)]
    [InlineData("0 8 * * *", "2026-03-15T08:01:00Z", false)]
    [InlineData("0 8 * * *", "2026-03-15T09:00:00Z", false)]
    // A list, and a range.
    [InlineData("0 9,17 * * *", "2026-03-15T17:00:00Z", true)]
    [InlineData("0 9,17 * * *", "2026-03-15T12:00:00Z", false)]
    [InlineData("30 9-17 * * *", "2026-03-15T14:30:00Z", true)]
    [InlineData("30 9-17 * * *", "2026-03-15T18:30:00Z", false)]
    // A step.
    [InlineData("*/15 * * * *", "2026-03-15T10:30:00Z", true)]
    [InlineData("*/15 * * * *", "2026-03-15T10:31:00Z", false)]
    // A day of the month, and a day of the week. 2026-03-15 is a Sunday.
    [InlineData("0 8 1 * *", "2026-03-01T08:00:00Z", true)]
    [InlineData("0 8 1 * *", "2026-03-15T08:00:00Z", false)]
    [InlineData("0 8 * * 0", "2026-03-15T08:00:00Z", true)]
    [InlineData("0 8 * * 1", "2026-03-15T08:00:00Z", false)]
    public void Minutes_match_or_do_not(string expression, string instant, bool expected) =>
        Assert.Equal(expected, CronSchedule.Matches(expression, Parse(instant)));

    /// <summary>
    /// Day-of-month and day-of-week are ORed when both are restricted.
    ///
    /// <para>What cron does, and what surprises everybody who reads it as an AND. "the 1st, and every
    /// Monday" — not "the 1st when it falls on a Monday". Pinned because the intuitive reading is the
    /// wrong one and somebody will eventually "fix" it.</para>
    /// </summary>
    [Theory]
    [InlineData("2026-03-01T08:00:00Z", true)]  // the 1st, a Sunday
    [InlineData("2026-03-02T08:00:00Z", true)]  // a Monday
    [InlineData("2026-03-03T08:00:00Z", false)] // neither
    public void Day_of_month_and_day_of_week_are_ored(string instant, bool expected) =>
        Assert.Equal(expected, CronSchedule.Matches("0 8 1 * 1", Parse(instant)));

    /// <summary>
    /// An expression nobody can read matches NOTHING.
    ///
    /// <para>The direction matters more than the behaviour. A malformed schedule that matched
    /// everything would run a workflow over every record every minute, and the first sign of it
    /// would be a notification table with a million rows in it.</para>
    /// </summary>
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

        // Across a whole day, minute by minute, so "never" means never rather than "not at the one
        // instant this test happened to pick".
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
