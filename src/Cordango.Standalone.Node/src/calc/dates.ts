// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

/**
 * The two shapes a stored date takes, kept apart the way the schema keeps them apart.
 *
 * A `date` column is a calendar day with no time and no zone; a `datetime` column is an instant.
 * The .NET runtime holds them as `DateOnly` and `DateTimeOffset` and picks duration arithmetic by
 * which pair it was handed — whole days between two dates, elapsed time between two instants, and
 * midnight-UTC promotion when the ends disagree. These two classes exist so this runtime can make
 * the same distinction instead of collapsing everything into a JavaScript `Date`, whose zone is
 * whatever machine happens to be running.
 */

/** A calendar day: what a `date` column holds. */
export class PlainDate {
  constructor(
    readonly year: number,
    readonly month: number,
    readonly day: number,
  ) {}

  /** `yyyy-MM-dd`, exactly — anything else is not a date value. Null otherwise. */
  static parse(text: string): PlainDate | null {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
    if (!match) return null;

    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    if (month < 1 || month > 12 || day < 1 || day > daysInMonth(year, month)) return null;

    return new PlainDate(year, month, day);
  }

  /**
   * Days since 0001-01-01 in the proleptic Gregorian calendar — the same number
   * `DateOnly.DayNumber` answers, so day arithmetic agrees digit for digit.
   */
  get dayNumber(): number {
    return daysFromCivil(this.year, this.month, this.day) + EpochDayNumber;
  }

  /** 1 January of the year 1 was a Monday. Sunday = 0, matching .NET's `DayOfWeek`. */
  get dayOfWeek(): number {
    return (this.dayNumber + 1) % 7;
  }

  get dayOfYear(): number {
    return this.dayNumber - new PlainDate(this.year, 1, 1).dayNumber + 1;
  }

  addDays(days: number): PlainDate {
    return PlainDate.fromDayNumber(this.dayNumber + days);
  }

  static fromDayNumber(dayNumber: number): PlainDate {
    const [year, month, day] = civilFromDays(dayNumber - EpochDayNumber);
    return new PlainDate(year, month, day);
  }

  /** Midnight UTC of this day — the promotion a mixed-ends duration uses. */
  atMidnightUtc(): Instant {
    return new Instant(BigInt(this.dayNumber - UnixEpochDayNumber) * 86_400_000_000n);
  }

  cmp(other: PlainDate): -1 | 0 | 1 {
    const a = this.dayNumber;
    const b = other.dayNumber;
    return a < b ? -1 : a > b ? 1 : 0;
  }

  toString(): string {
    return `${String(this.year).padStart(4, "0")}-${String(this.month).padStart(2, "0")}-${String(this.day).padStart(2, "0")}`;
  }
}

/** A moment in time: what a `datetime` column holds. Microseconds since the Unix epoch, in UTC. */
export class Instant {
  constructor(readonly epochMicros: bigint) {}

  /**
   * An ISO-8601 instant, offset or `Z`. Fractional seconds to microsecond precision — what
   * PostgreSQL's `timestamptz` carries. Null for anything else.
   */
  static parse(text: string): Instant | null {
    const match =
      /^(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(?:[Zz]|([+-])(\d{2}):(\d{2}))$/.exec(text);
    if (!match) return null;

    const date = PlainDate.parse(`${match[1]}-${match[2]}-${match[3]}`);
    if (!date) return null;

    const hour = Number(match[4]);
    const minute = Number(match[5]);
    const second = Number(match[6]);
    if (hour > 23 || minute > 59 || second > 59) return null;

    const micros = BigInt((match[7] ?? "").padEnd(6, "0").slice(0, 6) || "0");

    let offsetMinutes = 0n;
    if (match[8]) {
      offsetMinutes = BigInt(Number(match[9]) * 60 + Number(match[10]));
      if (match[8] === "-") offsetMinutes = -offsetMinutes;
    }

    const dayMicros =
      BigInt(hour) * 3_600_000_000n + BigInt(minute) * 60_000_000n + BigInt(second) * 1_000_000n + micros;

    return new Instant(
      BigInt(date.dayNumber - UnixEpochDayNumber) * 86_400_000_000n + dayMicros - offsetMinutes * 60_000_000n,
    );
  }

  /** The calendar day this instant falls on, read in UTC — the zone it is stored in, so the answer
   * does not move with the machine reading it. */
  utcDate(): PlainDate {
    return PlainDate.fromDayNumber(Number(floorDiv(this.epochMicros, 86_400_000_000n)) + UnixEpochDayNumber);
  }

  /** The hour of the day in UTC, 0-23. */
  utcHour(): number {
    return Number(floorDiv(floorMod(this.epochMicros, 86_400_000_000n), 3_600_000_000n));
  }

  cmp(other: Instant): -1 | 0 | 1 {
    return this.epochMicros < other.epochMicros ? -1 : this.epochMicros > other.epochMicros ? 1 : 0;
  }

  /** `yyyy-MM-ddTHH:mm:ss[.ffffff]+00:00` — invariant, UTC, micros only when they are not zero. */
  toString(): string {
    const date = this.utcDate();
    const dayMicros = floorMod(this.epochMicros, 86_400_000_000n);
    const seconds = dayMicros / 1_000_000n;
    const micros = dayMicros % 1_000_000n;

    const hh = String(seconds / 3600n).padStart(2, "0");
    const mm = String((seconds / 60n) % 60n).padStart(2, "0");
    const ss = String(seconds % 60n).padStart(2, "0");
    const frac = micros === 0n ? "" : "." + String(micros).padStart(6, "0").replace(/0+$/, "");

    return `${date.toString()}T${hh}:${mm}:${ss}${frac}+00:00`;
  }
}

/** Days between 0001-01-01 and 1970-01-01 — the offset between .NET day numbers and Unix days. */
const UnixEpochDayNumber = 719162;
const EpochDayNumber = 0;

function daysInMonth(year: number, month: number): number {
  if (month === 2) return isLeap(year) ? 29 : 28;
  return [31, 0, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1] ?? 30;
}

function isLeap(year: number): boolean {
  return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
}

/**
 * Days since 0001-01-01 for a proleptic Gregorian date. Howard Hinnant's civil-days algorithm,
 * shifted so day 0 is 0001-01-01 to match `DateOnly.DayNumber`.
 */
function daysFromCivil(year: number, month: number, day: number): number {
  const y = year - (month <= 2 ? 1 : 0);
  const era = Math.floor(y / 400);
  const yoe = y - era * 400;
  const doy = Math.trunc((153 * (month + (month > 2 ? -3 : 9)) + 2) / 5) + day - 1;
  const doe = yoe * 365 + Math.trunc(yoe / 4) - Math.trunc(yoe / 100) + doy;
  return era * 146097 + doe - 306;
}

function civilFromDays(dayNumber: number): [number, number, number] {
  const z = dayNumber + 306;
  const era = Math.floor(z / 146097);
  const doe = z - era * 146097;
  const yoe = Math.trunc((doe - Math.trunc(doe / 1460) + Math.trunc(doe / 36524) - Math.trunc(doe / 146096)) / 365);
  const y = yoe + era * 400;
  const doy = doe - (365 * yoe + Math.trunc(yoe / 4) - Math.trunc(yoe / 100));
  const mp = Math.trunc((5 * doy + 2) / 153);
  const day = doy - Math.trunc((153 * mp + 2) / 5) + 1;
  const month = mp < 10 ? mp + 3 : mp - 9;
  return [y + (month <= 2 ? 1 : 0), month, day];
}

function floorDiv(a: bigint, b: bigint): bigint {
  const q = a / b;
  return a % b !== 0n && (a < 0n) !== (b < 0n) ? q - 1n : q;
}

function floorMod(a: bigint, b: bigint): bigint {
  return a - floorDiv(a, b) * b;
}
