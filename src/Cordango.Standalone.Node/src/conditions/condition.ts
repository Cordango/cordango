// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

/**
 * A question about a record that answers yes or no: a command's guard, a workflow's `when`, a
 * filter on a rollup. One shape for all three, because the definition uses one shape for all three.
 *
 * The expected value is a string and the record's value is not — deliberately. What a definition
 * writes is text (`"closed"`, `"6"`, `"{{today+7}}"`); what a record holds is typed JSON. The
 * evaluator compares them numerically when both sides read as numbers and ordinally otherwise.
 */
export type Condition = {
  /** The field to read. Absent on a composite. */
  field?: string;
  /** One of the thirteen operators the language defines. Absent on a composite. */
  operator?: string;
  /** What to compare against, for the operators that take one value. */
  value?: string;
  /** What to compare against, for `in`, `notIn`, `between` and `overlaps`. */
  values?: string[];
  /** The other end of the record's own range, for `overlaps`. */
  endField?: string;
  /** A one-hop reference into another record — `room.requires_approval`. */
  path?: string;
  /** Every child must hold. */
  all?: Condition[];
  /** At least one child must hold. */
  any?: Condition[];
  /** The child must not hold. */
  not?: Condition;
};

/**
 * A condition as the definition's JSON spells it. `value` may be a scalar or an array — the array
 * spelling feeds `values` — and a scalar that is not a string is carried as its JSON text, which is
 * how a numeric `value: 6` meets a decimal column correctly.
 */
export function readCondition(json: unknown): Condition | null {
  if (json === null || typeof json !== "object" || Array.isArray(json)) return null;
  const leaf = json as Record<string, unknown>;

  if (Array.isArray(leaf["all"])) return composite(leaf["all"], (children) => ({ all: children }));
  if (Array.isArray(leaf["any"])) return composite(leaf["any"], (children) => ({ any: children }));
  if (leaf["not"] !== null && typeof leaf["not"] === "object" && !Array.isArray(leaf["not"])) {
    const child = readCondition(leaf["not"]);
    return child === null ? null : { not: child };
  }

  const condition: Condition = {};
  if (typeof leaf["field"] === "string") condition.field = leaf["field"];
  if (typeof leaf["operator"] === "string") condition.operator = leaf["operator"];
  if (typeof leaf["path"] === "string") condition.path = leaf["path"];
  if (typeof leaf["endField"] === "string") condition.endField = leaf["endField"];

  const value = leaf["value"] ?? leaf["values"];
  if (Array.isArray(value)) condition.values = value.map(scalar);
  else if (value !== undefined) condition.value = scalar(value);

  return condition;
}

function composite(children: unknown[], build: (children: Condition[]) => Condition): Condition | null {
  const read = children.map(readCondition).filter((c): c is Condition => c !== null);
  return read.length === 0 ? null : build(read);
}

function scalar(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "string") return value;
  return JSON.stringify(value);
}
