// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

/**
 * Exact decimal arithmetic, because a business figure must not pick up float dust.
 *
 * The .NET runtime computes every total in `decimal`; JavaScript's `number` is a double, and
 * `0.1 + 0.2` in a money column is the kind of wrong that survives every test that only checks
 * round figures. So values are a scaled BigInt — `units / 10^scale` — and addition, subtraction,
 * multiplication and comparison are exact.
 *
 * Division is the one operation whose exact answer can fail to terminate. It is computed to 28
 * fractional digits and rounded half-even there, which is the same order of precision C# `decimal`
 * carries; the two implementations can differ in the 28th digit of a non-terminating quotient and
 * nowhere else. `pow` goes through the platform's floating point on BOTH sides by design — the C#
 * runtime routes it through `double` too — so its answers agree by construction.
 *
 * One visible difference from C#, accepted on purpose: results are normalised, so `1.10 + 0.90`
 * prints as `2` rather than `2.00`. C# decimal preserves trailing zeros in its wire form; a JSON
 * reader treats the two spellings as the same number, which is the level this runtime promises
 * parity at.
 */
export class Dec {
  private constructor(
    /** The digits, sign included. */
    readonly units: bigint,
    /** How many of the digits sit right of the point. Always 0..MaxScale after normalisation. */
    readonly scale: number,
  ) {}

  static readonly MaxScale = 28;

  static readonly zero = new Dec(0n, 0);
  static readonly one = new Dec(1n, 0);

  /** Parse a plain decimal literal: optional sign, digits, at most one point. Null when it is not one. */
  static parse(text: string): Dec | null {
    const match = /^([+-]?)(?:(\d+)(?:\.(\d*))?|\.(\d+))$/.exec(text.trim());
    if (!match) return null;

    const sign = match[1] === "-" ? -1n : 1n;
    const whole = match[2] ?? "0";
    const frac = match[3] ?? match[4] ?? "";

    return Dec.normalised(sign * BigInt(whole + frac), frac.length);
  }

  /**
   * A JavaScript number, read through its shortest round-trip string — `0.3` arrives as the exact
   * decimal `0.3` rather than as the double nearest it. Null for anything that is not finite.
   */
  static from(value: number): Dec | null {
    if (!Number.isFinite(value)) return null;
    const text = String(value);
    const exp = /^([+-]?)(\d+)(?:\.(\d+))?e([+-]?\d+)$/i.exec(text);
    if (!exp) return Dec.parse(text);

    const sign = exp[1] === "-" ? -1n : 1n;
    const digits = sign * BigInt((exp[2] ?? "0") + (exp[3] ?? ""));
    const scale = (exp[3] ?? "").length - Number(exp[4]);

    return scale <= 0
      ? Dec.normalised(digits * 10n ** BigInt(-scale), 0)
      : Dec.normalised(digits, scale);
  }

  add(other: Dec): Dec {
    const [a, b, scale] = align(this, other);
    return Dec.normalised(a + b, scale);
  }

  sub(other: Dec): Dec {
    const [a, b, scale] = align(this, other);
    return Dec.normalised(a - b, scale);
  }

  mul(other: Dec): Dec {
    return Dec.normalised(this.units * other.units, this.scale + other.scale);
  }

  /** Null on a zero divisor: `x / 0` is unknown, not zero and not an error. */
  div(other: Dec): Dec | null {
    if (other.units === 0n) return null;
    if (this.units === 0n) return Dec.zero;

    const negative = this.units < 0n !== other.units < 0n;
    const dividend = abs(this.units);
    const divisor = abs(other.units);

    // Scale the dividend so the integer quotient carries MaxScale fractional digits in the
    // result's terms, then round half-even on what remains.
    const lift = Dec.MaxScale - this.scale + other.scale;
    const scaled = lift > 0 ? dividend * 10n ** BigInt(lift) : dividend;
    const resultScale = lift > 0 ? Dec.MaxScale : this.scale - other.scale;

    const magnitude = roundHalfEven(scaled / divisor, scaled % divisor, divisor);
    return Dec.normalised(negative ? -magnitude : magnitude, resultScale);
  }

  neg(): Dec {
    return new Dec(-this.units, this.scale);
  }

  cmp(other: Dec): -1 | 0 | 1 {
    const [a, b] = align(this, other);
    return a < b ? -1 : a > b ? 1 : 0;
  }

  eq(other: Dec): boolean {
    return this.cmp(other) === 0;
  }

  isZero(): boolean {
    return this.units === 0n;
  }

  /** The nearest double, for the one operation (`pow`) that goes through floating point on purpose. */
  toNumber(): number {
    return Number(this.toString());
  }

  toString(): string {
    const digits = abs(this.units).toString().padStart(this.scale + 1, "0");
    const point = digits.length - this.scale;
    const whole = digits.slice(0, point);
    const frac = digits.slice(point);
    return (this.units < 0n ? "-" : "") + (frac.length > 0 ? `${whole}.${frac}` : whole);
  }

  toJSON(): number {
    return this.toNumber();
  }

  /** Strip trailing zeros, and fold anything beyond MaxScale back with half-even rounding. */
  private static normalised(units: bigint, scale: number): Dec {
    let u = units;
    let s = scale;

    if (s > Dec.MaxScale) {
      const divisor = 10n ** BigInt(s - Dec.MaxScale);
      const magnitude = roundHalfEven(abs(u) / divisor, abs(u) % divisor, divisor);
      u = u < 0n ? -magnitude : magnitude;
      s = Dec.MaxScale;
    }

    while (s > 0 && u % 10n === 0n) {
      u /= 10n;
      s--;
    }
    if (u === 0n) s = 0;
    return new Dec(u, s);
  }
}

function abs(value: bigint): bigint {
  return value < 0n ? -value : value;
}

/** All arguments non-negative; `remainder`/`divisor` decide whether `quotient` steps up. */
function roundHalfEven(quotient: bigint, remainder: bigint, divisor: bigint): bigint {
  if (remainder === 0n) return quotient;
  const twice = 2n * remainder;
  if (twice > divisor || (twice === divisor && quotient % 2n !== 0n)) return quotient + 1n;
  return quotient;
}

function align(a: Dec, b: Dec): [bigint, bigint, number] {
  if (a.scale === b.scale) return [a.units, b.units, a.scale];
  if (a.scale > b.scale) return [a.units, b.units * 10n ** BigInt(a.scale - b.scale), a.scale];
  return [a.units * 10n ** BigInt(b.scale - a.scale), b.units, b.scale];
}
