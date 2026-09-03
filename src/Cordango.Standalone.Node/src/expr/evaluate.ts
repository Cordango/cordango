// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import * as calc from "../calc/calc.js";
import { hop, type Kind, type Node } from "./parse.js";

/**
 * Working a computed expression out over one record.
 *
 * The dotnet-vue target compiles each expression into a C# method; this runtime interprets the same
 * tree instead, and the two must produce the same figures — pinned as data in
 * `tests/fixtures/computed/`, which both suites run.
 *
 * The value model is the fixtures' own rule. **A blank field is zero. A figure that could not be
 * worked out is unknown. They are not the same.** So a number field reads as its value or zero, a
 * boolean field as its value or false — the blank is absorbed at the read — while `null` anywhere
 * past that point means unknown, and unknown spreads: through arithmetic, through comparisons, and
 * through boolean logic by Kleene's rules, where `unknown and false` is still false and
 * `unknown or true` is still true.
 */

/** A stored field's value, typed the way the schema types it. `null` is a blank; `undefined` is a
 * field the reader cannot answer at all (an unresolved hop), which reads the same as blank. */
export type FieldValue = Dec | boolean | PlainDate | Instant | null | undefined;

export type EvaluateOptions = {
  /** Which day begins a week here — the app's own `weekStart`, never a machine's. */
  weekStartsMonday: boolean;
  /** Reads one field of the record: a bare key, or `reference.field` for one hop. */
  read: (identifier: string) => FieldValue;
  /** The previous row of an ordered series, for `prev(field)`. Absent rows answer `"none"`, which
   * falls back to the seed. Omitted entirely (no series in scope) makes every `prev` unknown. */
  prev?: (field: string) => Dec | null | "none";
};

export type Value = Dec | boolean | PlainDate | Instant | null;

/** The expression's answer: a number, a boolean or a date by its static kind, or null for unknown. */
export function evaluate(node: Node, options: EvaluateOptions): Value {
  switch (node.type) {
    case "number":
      return node.value;
    case "boolean":
      return node.value;

    case "field": {
      switch (node.kind) {
        case "number":
          return asNumber(options.read(node.key)) ?? Dec.zero;
        case "boolean":
          return asBoolean(options.read(node.key)) ?? false;
        case "date":
          return dateOrNull(options.read(node.key));
      }
      break;
    }

    case "unary": {
      if (node.op === "-") {
        const operand = number(node.operand, options);
        return operand === null ? null : operand.neg();
      }
      const operand = boolean(node.operand, options);
      return operand === null ? null : !operand;
    }

    case "binary":
      return binary(node, options);

    case "function": {
      const left = number(node.left, options);
      const right = number(node.right, options);
      switch (node.name) {
        case "pow":
          return calc.power(left, right);
        case "min":
          return calc.min(left, right);
        case "max":
          return calc.max(left, right);
      }
      break;
    }

    case "duration": {
      const from = dateOrNull(options.read(node.from));
      const to = dateOrNull(options.read(node.to));
      switch (node.name) {
        case "days_between":
          return calc.daysBetween(from, to);
        case "hours_between":
          return calc.hoursBetween(from, to);
        case "minutes_between":
          return calc.minutesBetween(from, to);
      }
      return null;
    }

    case "datePart": {
      const value = dateOrNull(options.read(node.field));
      switch (node.name) {
        case "weekday":
          return calc.weekday(value, options.weekStartsMonday);
        case "week_of_year":
          return calc.weekOfYear(value, options.weekStartsMonday);
        case "month_of":
          return calc.monthOf(value);
        case "day_of_month":
          return calc.dayOfMonth(value);
        case "day_of_year":
          return calc.dayOfYear(value);
        case "year_of":
          return calc.yearOf(value);
        case "hour_of":
          return calc.hourOf(value);
      }
      return null;
    }

    case "dateBoundary": {
      const value = dateOrNull(options.read(node.field));
      switch (node.name) {
        case "start_of_week":
          return calc.startOfWeek(value, options.weekStartsMonday);
        case "end_of_week":
          return calc.endOfWeek(value, options.weekStartsMonday);
        case "start_of_month":
          return calc.startOfMonth(value);
        case "end_of_month":
          return calc.endOfMonth(value);
      }
      return null;
    }

    case "prev": {
      if (!options.prev) return null;
      const previous = options.prev(node.field);
      if (previous !== "none") return previous;
      return node.seed === null ? Dec.zero : number(node.seed, options);
    }
  }
  return null;
}

function binary(
  node: Extract<Node, { type: "binary" }>,
  options: EvaluateOptions,
): Value {
  switch (node.op) {
    case "+":
    case "-":
    case "*": {
      const left = number(node.left, options);
      const right = number(node.right, options);
      if (left === null || right === null) return null;
      return node.op === "+" ? left.add(right) : node.op === "-" ? left.sub(right) : left.mul(right);
    }

    case "/":
      return calc.divide(number(node.left, options), number(node.right, options));

    case "<":
    case "<=":
    case ">":
    case ">=": {
      const op = node.op;
      if (node.left.kind === "date")
        return calc.orderedDates(date(node.left, options), date(node.right, options), op);
      return calc.compare(number(node.left, options), number(node.right, options), op);
    }

    case "==":
    case "!=": {
      const wantSame = node.op === "==";
      switch (node.left.kind) {
        case "number": {
          const left = number(node.left, options);
          const right = number(node.right, options);
          return wantSame ? calc.same(left, right) : calc.different(left, right);
        }
        case "boolean": {
          const left = boolean(node.left, options);
          const right = boolean(node.right, options);
          return wantSame ? calc.sameBool(left, right) : calc.differentBool(left, right);
        }
        case "date": {
          const left = date(node.left, options);
          const right = date(node.right, options);
          return wantSame ? calc.sameDate(left, right) : calc.differentDate(left, right);
        }
      }
      break;
    }

    // Kleene logic: the definite half of an answer survives an unknown other half.
    case "and": {
      const left = boolean(node.left, options);
      const right = boolean(node.right, options);
      if (left === false || right === false) return false;
      if (left === null || right === null) return null;
      return true;
    }
    case "or": {
      const left = boolean(node.left, options);
      const right = boolean(node.right, options);
      if (left === true || right === true) return true;
      if (left === null || right === null) return null;
      return false;
    }
  }
  return null;
}

function number(node: Node, options: EvaluateOptions): Dec | null {
  const value = evaluate(node, options);
  return value instanceof Dec ? value : null;
}

function boolean(node: Node, options: EvaluateOptions): boolean | null {
  const value = evaluate(node, options);
  return typeof value === "boolean" ? value : null;
}

function date(node: Node, options: EvaluateOptions): calc.DateValue | null {
  const value = evaluate(node, options);
  return value instanceof PlainDate || value instanceof Instant ? value : null;
}

function asNumber(value: FieldValue): Dec | null {
  return value instanceof Dec ? value : null;
}

function asBoolean(value: FieldValue): boolean | null {
  return typeof value === "boolean" ? value : null;
}

function dateOrNull(value: FieldValue): calc.DateValue | null {
  return value instanceof PlainDate || value instanceof Instant ? value : null;
}

/** How a field's stored JSON becomes a typed value, given the schema's type for it. */
export function fieldValue(type: string, raw: unknown): FieldValue {
  if (raw === null || raw === undefined || raw === "") return null;

  switch (type) {
    case "integer":
    case "decimal":
    case "money":
    case "number":
      if (raw instanceof Dec) return raw;
      if (typeof raw === "number") return Dec.from(raw);
      if (typeof raw === "string") return Dec.parse(raw);
      return null;

    case "boolean":
      return typeof raw === "boolean" ? raw : null;

    case "date":
      if (raw instanceof PlainDate) return raw;
      return typeof raw === "string" ? PlainDate.parse(raw) : null;

    case "datetime":
      if (raw instanceof Instant) return raw;
      return typeof raw === "string" ? Instant.parse(raw) : null;

    default:
      return null;
  }
}

/** A reader over one record given the schema's field types, following one-hop identifiers through
 * `resolve` when the caller can. */
export function recordReader(
  fields: ReadonlyMap<string, string>,
  record: Record<string, unknown>,
  resolve?: (reference: string, field: string) => { type: string; raw: unknown } | null,
): (identifier: string) => FieldValue {
  return (identifier) => {
    const hopped = hop(identifier);
    if (hopped !== null) {
      const target = resolve?.(hopped.reference, hopped.field);
      return target ? fieldValue(target.type, target.raw) : undefined;
    }
    const type = fields.get(identifier);
    if (type === undefined) return undefined;
    return fieldValue(type, record[identifier]);
  };
}

/** The field-kind view of a schema's types, for the parser. */
export function kindOf(type: string): Kind | null {
  switch (type) {
    case "integer":
    case "decimal":
    case "money":
    case "number":
      return "number";
    case "boolean":
      return "boolean";
    case "date":
    case "datetime":
      return "date";
    default:
      return null;
  }
}
