// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Instant } from "../calc/dates.js";
import type { Condition } from "./condition.js";
import { resolveValue } from "./value-tokens.js";

/**
 * Does this record satisfy this condition?
 *
 * Used by a command's guard, a workflow's `when`, and a rollup's filters. One implementation,
 * because they are one language — and a PORT of the C# runtime's, held to it and to the platform's
 * by `tests/fixtures/conditions/*.json`, which all three suites run. Drift becomes a red test rather
 * than a wrong answer in production.
 *
 * Nothing throws. A malformed condition is false: the gate rejects one at author time, so reaching
 * here with a broken condition means something upstream failed — and the safe reading of "I do not
 * understand this guard" is that the guarded thing does not happen.
 */

/** The record, as its JSON serialisation holds it. Values are whatever JSON parsing produced. */
export type ConditionRecord = Record<string, unknown>;

/** Reads `referenceField.targetField` off a referenced record. Null when the reference is empty or
 * unresolved, in which case the leaf simply does not match. */
export type RecordHop = (record: ConditionRecord, referenceField: string, targetField: string) => unknown;

export function evaluateCondition(
  condition: Condition | null | undefined,
  record: ConditionRecord,
  actorId: string | null = null,
  now: Instant = new Instant(0n),
  hop: RecordHop | null = null,
): boolean {
  if (condition === null || condition === undefined) return true;

  if (condition.all && condition.all.length > 0)
    return condition.all.every((child) => evaluateCondition(child, record, actorId, now, hop));

  if (condition.any && condition.any.length > 0)
    return condition.any.some((child) => evaluateCondition(child, record, actorId, now, hop));

  if (condition.not) return !evaluateCondition(condition.not, record, actorId, now, hop);

  const op = condition.operator;
  if (op === undefined) return false;

  const operand = readOperand(condition, record, hop);
  if (!operand.found) return false;
  const actual = operand.value;

  const expected = resolveValue(condition.value ?? null, actorId, null, now);

  switch (op) {
    case "isEmpty":
      return isEmpty(actual);
    case "isNotEmpty":
      return !isEmpty(actual);
    case "eq":
      return compare(actual, expected) === 0;
    case "neq":
      return compare(actual, expected) !== 0;
    case "gt": {
      const c = compare(actual, expected);
      return c !== null && c !== Incomparable && c > 0;
    }
    case "gte": {
      const c = compare(actual, expected);
      return c !== null && c !== Incomparable && c >= 0;
    }
    case "lt": {
      const c = compare(actual, expected);
      return c !== null && c !== Incomparable && c < 0;
    }
    case "lte": {
      const c = compare(actual, expected);
      return c !== null && c !== Incomparable && c <= 0;
    }
    case "contains":
      return asString(actual).toUpperCase().includes((expected ?? "").toUpperCase());
    case "in":
      return inList(actual, condition, actorId, now);
    // A blank is NOT in the list, so `notIn` is true for a record nobody has filled in.
    // Debatable, and settled: the platform answers the same.
    case "notIn":
      return !inList(actual, condition, actorId, now);
    case "between":
      return between(actual, condition, actorId, now);
    case "overlaps":
      return overlaps(condition, record, actual, actorId, now);
    default:
      return false;
  }
}

/** What the leaf reads: the record's own field, or one hop through a reference. Not found when it
 * names neither, so a condition can never match by comparing nothing against nothing. */
function readOperand(
  leaf: Condition,
  record: ConditionRecord,
  hop: RecordHop | null,
): { found: boolean; value: unknown } {
  if (leaf.field !== undefined && leaf.field.length > 0) return { found: true, value: record[leaf.field] };

  if (leaf.path !== undefined && leaf.path.length > 0 && hop !== null) {
    const dot = leaf.path.indexOf(".");
    if (dot <= 0 || dot === leaf.path.length - 1) return { found: false, value: null };
    return { found: true, value: hop(record, leaf.path.slice(0, dot), leaf.path.slice(dot + 1)) };
  }

  return { found: false, value: null };
}

function expectedList(leaf: Condition, actorId: string | null, now: Instant): (string | null)[] {
  return (leaf.values ?? []).map((v) => resolveValue(v, actorId, null, now));
}

function inList(actual: unknown, leaf: Condition, actorId: string | null, now: Instant): boolean {
  return expectedList(leaf, actorId, now).some((e) => compare(actual, e) === 0);
}

/** Inclusive on both ends. "Due in the next 7 days" is `between ["{{today}}", "{{today+7}}"]`, and
 * both endpoints count. */
function between(actual: unknown, leaf: Condition, actorId: string | null, now: Instant): boolean {
  const range = expectedList(leaf, actorId, now);
  if (range.length !== 2 || isEmpty(actual)) return false;

  const low = compare(actual, range[0]!);
  const high = compare(actual, range[1]!);
  if (low === null || low === Incomparable || high === null || high === Incomparable) return false;

  return low >= 0 && high <= 0;
}

/**
 * Does the record's own range `[field, endField]` overlap the window it is given?
 *
 * The boundary rule depends on whether the endpoints carry a time. A bare date names a whole day,
 * so a task ending Monday DOES overlap a window starting Monday. Anything with a time is half-open:
 * a booking ending at 10:00 does NOT collide with one starting at 10:00, which is the entire point
 * of a conflict check. The front end applies the identical rule.
 */
function overlaps(
  leaf: Condition,
  record: ConditionRecord,
  start: unknown,
  actorId: string | null,
  now: Instant,
): boolean {
  const window = expectedList(leaf, actorId, now);
  if (window.length !== 2) return false;
  if (leaf.endField === undefined || leaf.endField.length === 0) return false;

  const end = record[leaf.endField];
  if (isEmpty(start) || isEmpty(end)) return false;
  if (window[0] === null || window[0] === "" || window[1] === null || window[1] === "") return false;

  const startVersusTo = compare(start, window[1]!);
  const endVersusFrom = compare(end, window[0]!);
  if (startVersusTo === null || startVersusTo === Incomparable) return false;
  if (endVersusFrom === null || endVersusFrom === Incomparable) return false;

  return dateOnlyValue(asString(start)) && dateOnlyValue(asString(end)) && dateOnlyValue(window[0]!) && dateOnlyValue(window[1]!)
    ? startVersusTo <= 0 && endVersusFrom >= 0
    : startVersusTo < 0 && endVersusFrom > 0;
}

/** A bare calendar date rather than an instant. */
function dateOnlyValue(value: string): boolean {
  if (value.length !== 10) return false;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return false;
  const month = Number(match[2]);
  const day = Number(match[3]);
  return month >= 1 && month <= 12 && day >= 1 && day <= 31;
}

/** Not a real ordering — the answer to "compare these two" when they cannot be. Every ordered
 * operator checks for it, so `gt` against a blank is false rather than true by accident. */
const Incomparable = Number.MIN_SAFE_INTEGER;

function compare(actual: unknown, expected: string | null): number | null {
  const actualEmpty = isEmpty(actual);
  const expectedEmpty = expected === null || expected === "";

  if (actualEmpty || expectedEmpty) return actualEmpty && expectedEmpty ? 0 : Incomparable;

  const a = asNumber(actual);
  const b = asNumberText(expected!);
  if (a !== null && b !== null) return a < b ? -1 : a > b ? 1 : 0;

  return ordinal(asString(actual), expected!);
}

function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function isEmpty(value: unknown): boolean {
  return (
    value === null ||
    value === undefined ||
    (typeof value === "string" && value.length === 0) ||
    (Array.isArray(value) && value.length === 0)
  );
}

/** A value as a number, whatever JSON backed it by — the double the C# twin reads through its
 * invariant JSON text. */
function asNumber(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  if (typeof value === "string") return asNumberText(value);
  return null;
}

function asNumberText(value: string): number | null {
  if (value.trim().length === 0) return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function asString(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "string") return value;
  return JSON.stringify(value);
}
