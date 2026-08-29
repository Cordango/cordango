// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Records;

/// <summary>
/// The arithmetic a computed field is made of, where plain C# operators would answer differently
/// from the definition.
///
/// <para><b>This is not an expression engine.</b> A computed field is emitted as a real C# method —
/// <c>(r.Revenue ?? 0m) - (r.Costs ?? 0m)</c> — and most of it needs nothing from here. What does is
/// the handful of places where the language has a rule and C# has a different one, and each of those
/// is a method rather than an inline expression because a rule repeated at forty call sites is a
/// rule that will eventually be repeated wrong.</para>
///
/// <para><b>The rule underneath all of them: unknown is not zero and not false.</b> A blank NUMBER
/// field reads as zero, because a record with no tax has a tax of nothing and a total that refused
/// to add it up would be blank on every half-filled row. But a figure that could not be WORKED OUT —
/// divided by zero, compared against a blank, raised to a power that overflows — is unknown, and it
/// stays unknown through everything downstream. A cap computed from an unknown bound must not
/// silently become no cap at all.</para>
/// </summary>
public static class Calc
{
    /// <summary>
    /// Division, which is the one arithmetic operator that can fail to have an answer.
    ///
    /// <para><c>x / 0</c> is neither zero nor an exception here: it is unknown. A runway of "cash
    /// divided by monthly burn" on a plan with no costs yet is not infinite and not zero — nobody
    /// knows it — and a plan that showed 0 months of runway because nothing had been entered would
    /// be alarming and wrong.</para>
    /// </summary>
    public static decimal? Divide(decimal? left, decimal? right) =>
        right is null or 0m ? null : left / right;

    /// <summary>The lower of two figures — a usage charge capped at a plan's ceiling. Null in, null
    /// out: a bound that could not be worked out is not the same as no bound, and returning the
    /// other side would silently un-cap the figure.</summary>
    public static decimal? Min(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? Math.Min(a, b) : null;

    /// <summary>The higher of two figures — a balance floored at zero. Same discipline as
    /// <see cref="Min"/>.</summary>
    public static decimal? Max(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? Math.Max(a, b) : null;

    /// <summary>
    /// Raising to a power, for compound growth and annualised returns.
    ///
    /// <para>Through <c>double</c>, because <c>decimal</c> has no power operator — and back through
    /// a checked conversion, because the round trip can overflow and an OverflowException from
    /// inside a total is a 500 on a page somebody was only reading.</para>
    /// </summary>
    public static decimal? Power(decimal? value, decimal? exponent)
    {
        if (value is not { } v || exponent is not { } e) return null;

        var result = Math.Pow((double)v, (double)e);
        if (double.IsNaN(result) || double.IsInfinity(result)) return null;

        try
        {
            return (decimal)result;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// An ordered comparison that can answer "cannot say".
    ///
    /// <para>A bare <c>&lt;</c> on two nullables answers FALSE when either is null, which reads as
    /// "no" — so a rule keyed on "is the balance below the threshold" would fire on every record
    /// whose balance has not been computed yet. Here that is null, and a null never satisfies
    /// anything.</para>
    /// </summary>
    public static bool? Compare(decimal? left, decimal? right, string op)
    {
        if (left is not { } a || right is not { } b) return null;

        return op switch
        {
            "<" => a < b,
            "<=" => a <= b,
            ">" => a > b,
            ">=" => a >= b,
            _ => null,
        };
    }

    /// <summary>Equality that keeps "cannot say" separate from "no". Two unknowns are not equal —
    /// they are two things nobody knows.</summary>
    public static bool? Same(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? a == b : null;

    public static bool? Different(decimal? left, decimal? right) =>
        left is { } a && right is { } b ? a != b : null;

    public static bool? Same(bool? left, bool? right) =>
        left is { } a && right is { } b ? a == b : null;

    public static bool? Different(bool? left, bool? right) =>
        left is { } a && right is { } b ? a != b : null;

    /// <summary>Whole and fractional days between two instants, the way the definition means it:
    /// null unless both ends are known.</summary>
    public static decimal? Days(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalDays : null;

    public static decimal? Hours(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalHours : null;

    public static decimal? Minutes(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } a && to is { } b ? (decimal)(b - a).TotalMinutes : null;

    /// <summary>
    /// The same three durations over plain dates.
    ///
    /// <para>A <c>date</c> column is a <see cref="DateOnly"/> and a <c>datetime</c> is a
    /// <see cref="DateTimeOffset"/>, and a definition happily writes <c>days_between</c> over either.
    /// Converting at midnight UTC keeps "two days" meaning two days rather than one and a bit.</para>
    /// </summary>
    public static decimal? Days(DateOnly? from, DateOnly? to) =>
        from is { } a && to is { } b ? b.DayNumber - a.DayNumber : null;

    public static decimal? Hours(DateOnly? from, DateOnly? to) => Days(from, to) * 24m;

    public static decimal? Minutes(DateOnly? from, DateOnly? to) => Days(from, to) * 1440m;

    /// <summary>
    /// The same three durations with a date at one end and a datetime at the other.
    ///
    /// <para><b>These exist because the gate lets the two ends disagree.</b> A duration argument is
    /// checked one at a time — each has to be a <c>date</c> or a <c>datetime</c> field — and nothing
    /// checks that the pair agree. So <c>minutes_between(shift_date, clocked_in_at)</c> passes
    /// <c>cordango check</c>, and without these overloads it emitted a call that bound to neither
    /// same-typed pair: CS1503, at <c>dotnet build</c>, in generated code the author never saw. An
    /// expression the language accepts has to produce an application that compiles.</para>
    ///
    /// <para>MIDNIGHT UTC is the promotion, the convention already named above. A bare date has no
    /// time and no zone, and there is no third thing in scope to borrow one from: the record's zone
    /// is not a column, and using the server's would make the same two records produce different
    /// answers on different hosts. So the day starts at 00:00Z and the duration is real elapsed time
    /// from there — <c>hours_between(shift_date, clocked_in_at)</c> on an 09:15Z clock-in is 9.25,
    /// which is the question somebody asking it meant.</para>
    ///
    /// <para>Not routed through the DateOnly pair: that one answers in whole days by DayNumber, and
    /// promoting a datetime down to a date to reach it would silently discard the time of day —
    /// turning a 9.25-hour answer into 0 and calling it agreement.</para>
    /// </summary>
    public static decimal? Days(DateOnly? from, DateTimeOffset? to) => Days(AtMidnightUtc(from), to);

    public static decimal? Days(DateTimeOffset? from, DateOnly? to) => Days(from, AtMidnightUtc(to));

    public static decimal? Hours(DateOnly? from, DateTimeOffset? to) => Hours(AtMidnightUtc(from), to);

    public static decimal? Hours(DateTimeOffset? from, DateOnly? to) => Hours(from, AtMidnightUtc(to));

    public static decimal? Minutes(DateOnly? from, DateTimeOffset? to) => Minutes(AtMidnightUtc(from), to);

    public static decimal? Minutes(DateTimeOffset? from, DateOnly? to) => Minutes(from, AtMidnightUtc(to));

    /// <summary>A bare date as the instant its day begins in UTC. Null stays null, so a missing end
    /// still makes the whole duration unknown rather than dating it to year one.</summary>
    private static DateTimeOffset? AtMidnightUtc(DateOnly? date) =>
        date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    /// <summary>
    /// The parts of one date.
    ///
    /// <para><b>A blank date has no parts.</b> Every one of these answers null rather than zero on a
    /// row where the date was never entered — the same rule the durations follow, and for the same
    /// reason: month 0 is not a month, and a list grouped by it would grow a bucket of records that
    /// simply have no date yet, sitting before January as though they were early.</para>
    ///
    /// <para><c>weekStart</c> is the app's own, passed in rather than assumed, because Monday and
    /// Sunday are both ordinary answers and neither is a safe default to bake into the runtime. The
    /// generator writes the app's setting into the call.</para>
    /// </summary>
    public static decimal? Weekday(DateOnly? date, bool weekStartsMonday) =>
        date is { } d ? (decimal)DayIndex(d, weekStartsMonday) + 1 : null;

    public static decimal? Weekday(DateTimeOffset? at, bool weekStartsMonday) =>
        Weekday(AsDate(at), weekStartsMonday);

    /// <summary>
    /// Which week of the year the date falls in, counting from the week containing 1 January.
    ///
    /// <para>Deliberately NOT ISO 8601 week numbering, which puts 1 January into week 52 or 53 of the
    /// previous year when the week starts late. That rule is correct and it is also the reason ISO
    /// weeks need a paired ISO year to mean anything — and a computed field here answers a single
    /// number with nowhere to put the second one. Counting from 1 January keeps
    /// <c>year_of</c> + <c>week_of_year</c> a pair that agrees with itself, which is what a list
    /// grouped by week actually needs.</para>
    /// </summary>
    public static decimal? WeekOfYear(DateOnly? date, bool weekStartsMonday)
    {
        if (date is not { } d) return null;

        var firstOfYear = new DateOnly(d.Year, 1, 1);
        var offsetIntoFirstWeek = DayIndex(firstOfYear, weekStartsMonday);
        return ((d.DayOfYear - 1 + offsetIntoFirstWeek) / 7) + 1;
    }

    public static decimal? WeekOfYear(DateTimeOffset? at, bool weekStartsMonday) =>
        WeekOfYear(AsDate(at), weekStartsMonday);

    public static decimal? MonthOf(DateOnly? date) => date is { } d ? d.Month : null;

    public static decimal? MonthOf(DateTimeOffset? at) => MonthOf(AsDate(at));

    public static decimal? DayOfMonth(DateOnly? date) => date is { } d ? d.Day : null;

    public static decimal? DayOfMonth(DateTimeOffset? at) => DayOfMonth(AsDate(at));

    public static decimal? DayOfYear(DateOnly? date) => date is { } d ? d.DayOfYear : null;

    public static decimal? DayOfYear(DateTimeOffset? at) => DayOfYear(AsDate(at));

    public static decimal? YearOf(DateOnly? date) => date is { } d ? d.Year : null;

    public static decimal? YearOf(DateTimeOffset? at) => YearOf(AsDate(at));

    /// <summary>The hour of a stored instant, 0-23. Only over a datetime: the gate refuses this over
    /// a <c>date</c> column, which has no time of day and would answer 0 on every row.</summary>
    public static decimal? HourOf(DateTimeOffset? at) => at is { } a ? a.UtcDateTime.Hour : null;

    /// <summary>How many days into its week the date sits, 0-6, under the given convention. The one
    /// place the week start is interpreted — everything week-shaped is built on this, so Monday and
    /// Sunday cannot drift apart between <c>weekday</c> and <c>week_of_year</c>.</summary>
    private static int DayIndex(DateOnly date, bool weekStartsMonday) =>
        weekStartsMonday
            ? ((int)date.DayOfWeek + 6) % 7
            : (int)date.DayOfWeek;

    /// <summary>The calendar day a stored instant falls on, read in UTC — the zone the instant is
    /// stored in, so the answer does not move with the machine reading it.</summary>
    private static DateOnly? AsDate(DateTimeOffset? at) =>
        at is { } a ? DateOnly.FromDateTime(a.UtcDateTime) : null;
}
