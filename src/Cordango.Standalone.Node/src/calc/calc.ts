// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Dec } from "./decimal.js";
import { Instant, PlainDate } from "./dates.js";

/**
 * The arithmetic a computed field is made of, where the obvious JavaScript would answer differently
 * from the definition.
 *
 * A port of the .NET runtime's `Calc`, rule for rule, held to it by `tests/fixtures/computed/`.
 * The rule underneath all of them: **unknown is not zero and not false.** A blank NUMBER field
 * reads as zero — a record with no tax has a tax of nothing. But a figure that could not be WORKED
 * OUT — divided by zero, compared against an unknown, raised to a power that overflows — is null
 * here, and it stays null through everything downstream. A cap computed from an unknown bound must
 * not silently become no cap at all.
 */

export type DateValue = PlainDate | Instant;

/** Division: `x / 0` is neither zero nor an error — nobody knows it, so nobody gets a number. */
export function divide(left: Dec | null, right: Dec | null): Dec | null {
  if (left === null || right === null) return null;
  return left.div(right);
}

/** The lower of two figures. Null in, null out: a bound that could not be worked out is not the
 * same as no bound, and returning the other side would silently un-cap the figure. */
export function min(left: Dec | null, right: Dec | null): Dec | null {
  if (left === null || right === null) return null;
  return left.cmp(right) <= 0 ? left : right;
}

export function max(left: Dec | null, right: Dec | null): Dec | null {
  if (left === null || right === null) return null;
  return left.cmp(right) >= 0 ? left : right;
}

/**
 * Raising to a power, through floating point on purpose — the C# runtime routes `pow` through
 * `double` too, so the answers agree by construction. Overflow and NaN are unknown rather than an
 * exception inside a total somebody was only reading.
 */
export function power(value: Dec | null, exponent: Dec | null): Dec | null {
  if (value === null || exponent === null) return null;

  const result = Math.pow(value.toNumber(), exponent.toNumber());
  if (!Number.isFinite(result)) return null;

  // C# decimal tops out just shy of 7.9e28; a double that survived the conversion there must
  // survive it here, and one that would not becomes the same unknown.
  if (Math.abs(result) >= 7.9e28) return null;
  return Dec.from(result);
}

/** An ordered comparison that can answer "cannot say": null when either side is unknown, so a rule
 * keyed on "is the balance below the threshold" never fires on a record whose balance has not been
 * computed yet. */
export function compare(left: Dec | null, right: Dec | null, op: "<" | "<=" | ">" | ">="): boolean | null {
  if (left === null || right === null) return null;

  const c = left.cmp(right);
  switch (op) {
    case "<":
      return c < 0;
    case "<=":
      return c <= 0;
    case ">":
      return c > 0;
    case ">=":
      return c >= 0;
  }
}

/** Equality that keeps "cannot say" separate from "no". Two unknowns are not equal — they are two
 * things nobody knows. */
export function same(left: Dec | null, right: Dec | null): boolean | null {
  if (left === null || right === null) return null;
  return left.eq(right);
}

export function different(left: Dec | null, right: Dec | null): boolean | null {
  if (left === null || right === null) return null;
  return !left.eq(right);
}

export function sameBool(left: boolean | null, right: boolean | null): boolean | null {
  if (left === null || right === null) return null;
  return left === right;
}

export function differentBool(left: boolean | null, right: boolean | null): boolean | null {
  if (left === null || right === null) return null;
  return left !== right;
}

export function sameDate(left: DateValue | null, right: DateValue | null): boolean | null {
  const c = compareDates(left, right);
  return c === null ? null : c === 0;
}

export function differentDate(left: DateValue | null, right: DateValue | null): boolean | null {
  const c = compareDates(left, right);
  return c === null ? null : c !== 0;
}

/** Two dates in order, promoting a bare date to midnight UTC when the shapes differ — the same
 * convention the durations use. Null when either end is unknown. */
export function compareDates(left: DateValue | null, right: DateValue | null): -1 | 0 | 1 | null {
  if (left === null || right === null) return null;
  if (left instanceof PlainDate && right instanceof PlainDate) return left.cmp(right);
  return asInstant(left).cmp(asInstant(right));
}

export function orderedDates(left: DateValue | null, right: DateValue | null, op: "<" | "<=" | ">" | ">="): boolean | null {
  const c = compareDates(left, right);
  if (c === null) return null;
  switch (op) {
    case "<":
      return c < 0;
    case "<=":
      return c <= 0;
    case ">":
      return c > 0;
    case ">=":
      return c >= 0;
  }
}

/**
 * Whole and fractional time between two stored dates, null unless both ends are known.
 *
 * Two bare dates answer in whole days by day number. Anything involving an instant is real elapsed
 * time — a bare date at one end is promoted to midnight UTC, so
 * `hours_between(shift_date, clocked_in_at)` on an 09:15Z clock-in is 9.25. The elapsed arithmetic
 * goes through a double exactly as the C# runtime's `TimeSpan.TotalDays` does.
 */
export function daysBetween(from: DateValue | null, to: DateValue | null): Dec | null {
  if (from === null || to === null) return null;
  if (from instanceof PlainDate && to instanceof PlainDate) return Dec.from(to.dayNumber - from.dayNumber);
  return elapsed(asInstant(from), asInstant(to), 86_400_000_000);
}

export function hoursBetween(from: DateValue | null, to: DateValue | null): Dec | null {
  if (from === null || to === null) return null;
  if (from instanceof PlainDate && to instanceof PlainDate)
    return Dec.from((to.dayNumber - from.dayNumber) * 24);
  return elapsed(asInstant(from), asInstant(to), 3_600_000_000);
}

export function minutesBetween(from: DateValue | null, to: DateValue | null): Dec | null {
  if (from === null || to === null) return null;
  if (from instanceof PlainDate && to instanceof PlainDate)
    return Dec.from((to.dayNumber - from.dayNumber) * 1440);
  return elapsed(asInstant(from), asInstant(to), 60_000_000);
}

/**
 * The parts of one date. A blank date has no parts — every one of these answers null rather than
 * zero on a row where the date was never entered, so a list grouped by month never grows a bucket
 * sitting before January.
 *
 * `weekStartsMonday` is the app's own convention, passed in rather than assumed.
 */
export function weekday(date: DateValue | null, weekStartsMonday: boolean): Dec | null {
  const d = asDate(date);
  return d === null ? null : Dec.from(dayIndex(d, weekStartsMonday) + 1);
}

/** Week of the year counting from the week containing 1 January — deliberately NOT ISO 8601, which
 * would need a paired ISO year this single number has nowhere to put. */
export function weekOfYear(date: DateValue | null, weekStartsMonday: boolean): Dec | null {
  const d = asDate(date);
  if (d === null) return null;

  const offsetIntoFirstWeek = dayIndex(new PlainDate(d.year, 1, 1), weekStartsMonday);
  return Dec.from(Math.trunc((d.dayOfYear - 1 + offsetIntoFirstWeek) / 7) + 1);
}

export function monthOf(date: DateValue | null): Dec | null {
  const d = asDate(date);
  return d === null ? null : Dec.from(d.month);
}

export function dayOfMonth(date: DateValue | null): Dec | null {
  const d = asDate(date);
  return d === null ? null : Dec.from(d.day);
}

export function dayOfYear(date: DateValue | null): Dec | null {
  const d = asDate(date);
  return d === null ? null : Dec.from(d.dayOfYear);
}

export function yearOf(date: DateValue | null): Dec | null {
  const d = asDate(date);
  return d === null ? null : Dec.from(d.year);
}

/** The hour of a stored instant, 0-23, in UTC. Only meaningful over a datetime; the gate refuses it
 * over a date column. */
export function hourOf(at: DateValue | null): Dec | null {
  if (at === null) return null;
  if (at instanceof PlainDate) return null;
  return Dec.from(at.utcHour());
}

/** The date a containing period begins on. The only arithmetic here that answers a DATE. */
export function startOfWeek(date: DateValue | null, weekStartsMonday: boolean): PlainDate | null {
  const d = asDate(date);
  return d === null ? null : d.addDays(-dayIndex(d, weekStartsMonday));
}

/** The last day of the week, INCLUSIVE — the Sunday of a Monday-start week. */
export function endOfWeek(date: DateValue | null, weekStartsMonday: boolean): PlainDate | null {
  const start = startOfWeek(date, weekStartsMonday);
  return start === null ? null : start.addDays(6);
}

export function startOfMonth(date: DateValue | null): PlainDate | null {
  const d = asDate(date);
  return d === null ? null : new PlainDate(d.year, d.month, 1);
}

/** The last day of the month, inclusive — worked out rather than assumed, so February is right in a
 * leap year without anybody thinking about it. */
export function endOfMonth(date: DateValue | null): PlainDate | null {
  const d = asDate(date);
  if (d === null) return null;
  const firstOfNext = d.month === 12 ? new PlainDate(d.year + 1, 1, 1) : new PlainDate(d.year, d.month + 1, 1);
  return firstOfNext.addDays(-1);
}

/** How many days into its week the date sits, 0-6, under the given convention. The one place the
 * week start is interpreted, so `weekday`, `week_of_year` and `start_of_week` cannot drift apart. */
function dayIndex(date: PlainDate, weekStartsMonday: boolean): number {
  return weekStartsMonday ? (date.dayOfWeek + 6) % 7 : date.dayOfWeek;
}

function asDate(value: DateValue | null): PlainDate | null {
  if (value === null) return null;
  return value instanceof PlainDate ? value : value.utcDate();
}

function asInstant(value: DateValue): Instant {
  return value instanceof PlainDate ? value.atMidnightUtc() : value;
}

/** Elapsed time in the given unit of microseconds, through a double the way `TimeSpan` totals are. */
function elapsed(from: Instant, to: Instant, unitMicros: number): Dec | null {
  return Dec.from(Number(to.epochMicros - from.epochMicros) / unitMicros);
}
