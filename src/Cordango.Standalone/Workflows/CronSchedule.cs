// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;

namespace Cordango.Standalone.Workflows;

/// <summary>
/// Does this minute match this cron expression?
///
/// <para>Five fields — minute, hour, day of month, month, day of week — each of which may be
/// <c>*</c>, a number, a list <c>1,15</c>, a range <c>9-17</c>, or a step <c>*/15</c>. That is the
/// notation everybody already knows, which is the whole argument for using it: a schedule is
/// something an operator reads at three in the morning.</para>
///
/// <para><b>Matching a minute rather than computing the next occurrence.</b> The scheduler wakes once
/// a minute and asks each expression whether NOW is one of its times. Computing the next occurrence
/// is the other way to build this and it needs a clock the process trusts across restarts, a stored
/// last-run per schedule, and an answer for what to do about the twelve occurrences missed while the
/// machine was off. Asking about the current minute has none of those questions: a schedule that did
/// not run because nothing was running did not run, which is what an operator assumes anyway.</para>
///
/// <para><b>Times are UTC.</b> A generated application has no per-user timezone at the scheduler
/// level, and "8am" meaning something different in June and December is a surprise nobody asked for.
/// The generated README says so.</para>
/// </summary>
public static class CronSchedule
{
    /// <summary>True when <paramref name="utc"/> falls in a minute the expression names. A malformed
    /// expression matches NOTHING rather than everything — a schedule that silently ran every minute
    /// would be far worse than one that never ran.</summary>
    public static bool Matches(string? expression, DateTimeOffset utc)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5) return false;

        var time = utc.UtcDateTime;

        // Day-of-month and day-of-week are ORed when both are restricted, which is what cron does and
        // what surprises people who expect an AND. "1 * 1" means the first of the month AND every
        // Monday, not the first of the month when it is a Monday.
        var dayOfMonth = Field(fields[2], time.Day, 1, 31);
        var dayOfWeek = Field(fields[4], (int)time.DayOfWeek, 0, 6);
        var byDay = fields[2] == "*" || fields[4] == "*" ? dayOfMonth && dayOfWeek : dayOfMonth || dayOfWeek;

        return Field(fields[0], time.Minute, 0, 59)
            && Field(fields[1], time.Hour, 0, 23)
            && Field(fields[3], time.Month, 1, 12)
            && byDay;
    }

    /// <summary>True when the expression is one this understands. Used by the generator, so a
    /// schedule nobody can read is reported at build time rather than never firing.</summary>
    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5) return false;

        var bounds = new[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 6) };
        for (var i = 0; i < 5; i++)
            if (!Readable(fields[i], bounds[i].Item1, bounds[i].Item2))
                return false;

        return true;
    }

    private static bool Field(string field, int value, int low, int high)
    {
        foreach (var term in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Term(term, value, low, high))
                return true;

        return false;
    }

    private static bool Term(string term, int value, int low, int high)
    {
        var step = 1;
        var slash = term.IndexOf('/', StringComparison.Ordinal);

        if (slash >= 0)
        {
            if (!int.TryParse(term[(slash + 1)..], CultureInfo.InvariantCulture, out step) || step <= 0) return false;
            term = term[..slash];
        }

        var (from, to) = Bounds(term, low, high);
        if (from is null || to is null) return false;
        if (value < from || value > to) return false;

        return (value - from.Value) % step == 0;
    }

    private static (int? From, int? To) Bounds(string term, int low, int high)
    {
        if (term is "*" or "") return (low, high);

        var dash = term.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
            return int.TryParse(term[..dash], CultureInfo.InvariantCulture, out var a)
                && int.TryParse(term[(dash + 1)..], CultureInfo.InvariantCulture, out var b)
                && a >= low && b <= high && a <= b
                    ? (a, b)
                    : (null, null);

        return int.TryParse(term, CultureInfo.InvariantCulture, out var single) && single >= low && single <= high
            ? (single, single)
            : (null, null);
    }

    private static bool Readable(string field, int low, int high)
    {
        foreach (var term in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = term;
            var slash = text.IndexOf('/', StringComparison.Ordinal);

            if (slash >= 0)
            {
                if (!int.TryParse(text[(slash + 1)..], CultureInfo.InvariantCulture, out var step) || step <= 0)
                    return false;
                text = text[..slash];
            }

            if (Bounds(text, low, high) is { From: null }) return false;
        }

        return true;
    }
}
