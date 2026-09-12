// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import { parse } from "../expr/parse.js";
import { evaluate, fieldValue, recordReader, type FieldValue, type Value } from "../expr/evaluate.js";
import type { RecordDescriptor, RecordRow } from "./descriptor.js";

/** What a computed field needs to know beyond the expression itself. */
export interface ComputedOptions {
  /** Which day begins a week here — the application's own `weekStart`, never the machine's. It
   * changes what `week_of()` answers, and a figure that differs by server locale is a figure nobody
   * can reconcile. */
  readonly weekStartsMonday: boolean;
  /** Reads a field off a referenced record, for an expression that hops. Absent means a hop cannot
   * be resolved, and an unresolved hop reads as blank rather than as an error. */
  readonly resolve?: (reference: string, field: string) => { type: string; raw: unknown } | null;
}

/**
 * One figure a record works out for itself.
 *
 * **Parsed once, here, at module load.** An expression that will not parse is a fault in the
 * application rather than in the record being written, so it surfaces when the application starts
 * and not on the first save six weeks later.
 *
 * **The arithmetic is the runtime's, and that is the point.** The same parser and the same
 * evaluator answer here, on the hosted platform and in the .NET target, against one set of decision
 * fixtures every implementation's suite runs. A generated application that did its own arithmetic
 * would be a fourth implementation of `if`, of three-valued unknown, and of what `0.1 + 0.2` means
 * to an invoice — and the first one to disagree would disagree silently.
 *
 * @param descriptor the entity this field belongs to, which is where the field types come from
 * @param target the field the answer is written to
 * @param expression the computed expression, as the definition wrote it
 */
export function computedField<T extends RecordRow>(
  descriptor: RecordDescriptor<T>,
  target: string,
  expression: string,
  options: ComputedOptions,
): (record: T) => void {
  const parsed = parse(expression, (key) => kindOfField(descriptor, key));

  if (parsed.node === null)
    throw new Error(
      `The computed expression for '${descriptor.entityKey}.${target}' could not be read: `
        + `${parsed.error ?? "unknown error"} (${expression})`,
    );

  const node = parsed.node;
  const field = descriptor.tryGetField(target);

  if (field === undefined)
    throw new Error(
      `'${descriptor.entityKey}' has no field '${target}' for a computed expression to write to.`,
    );

  const targetType = field.type;
  const types = new Map<string, string>(descriptor.fields.map((f) => [f.key, f.type]));

  return (record: T): void => {
    const read = recordReader(types, record as Record<string, unknown>, options.resolve);
    const answer = evaluate(node, { weekStartsMonday: options.weekStartsMonday, read });
    (record as Record<string, unknown>)[target] = coerce(answer, targetType);
  };
}

/** Run several computed fields in order. The order is the one the generator worked out from the
 * dependency graph: a figure that reads another figure is worked out after it. */
export function computeAll<T extends RecordRow>(
  steps: readonly ((record: T) => void)[],
): (record: T) => void {
  return (record: T): void => {
    for (const step of steps) step(record);
  };
}

/** The static kind the parser needs for a field it meets, or null when this entity has no such
 * field — which the parser reports as an error naming the field, rather than guessing. */
function kindOfField<T extends RecordRow>(
  descriptor: RecordDescriptor<T>,
  key: string,
): "number" | "boolean" | "date" | "text" | null {
  const field = descriptor.tryGetField(key);
  if (field === undefined) return null;

  switch (field.type) {
    case "integer":
    case "decimal":
    case "money":
      return "number";
    case "boolean":
      return "boolean";
    case "date":
    case "datetime":
      return "date";
    default:
      return "text";
  }
}

/**
 * The answer, in the shape the target field stores.
 *
 * `null` stays `null` throughout: unknown is not zero, and writing a zero where the expression
 * could not reach an answer is how a dash on a screen becomes a wrong number in a total.
 */
function coerce(answer: Value, targetType: string): unknown {
  if (answer === null) return null;

  switch (targetType) {
    case "integer":
      // Truncated toward zero, the way an integer column stores a fractional answer everywhere
      // else in this runtime.
      return answer instanceof Dec ? Math.trunc(answer.toJSON()) : null;
    case "decimal":
    case "money":
      return answer instanceof Dec ? answer : null;
    case "boolean":
      return typeof answer === "boolean" ? answer : null;
    case "date":
      if (answer instanceof PlainDate) return answer;
      if (answer instanceof Instant) return PlainDate.parse(answer.toString().slice(0, 10));
      return null;
    case "datetime":
      return answer instanceof Instant ? answer : null;
    default:
      return textOf(answer);
  }
}

function textOf(answer: Value): string | null {
  if (typeof answer === "string") return answer;
  if (typeof answer === "boolean") return answer ? "true" : "false";
  if (answer instanceof Dec || answer instanceof PlainDate || answer instanceof Instant)
    return answer.toString();
  return null;
}

/** Re-exported so a generated computed file needs one import rather than three. */
export { fieldValue, type FieldValue };
