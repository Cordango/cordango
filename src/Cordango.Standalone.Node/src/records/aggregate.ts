// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See the project root for details.

import { Dec } from "../calc/decimal.js";
import { PlainDate } from "../calc/dates.js";
import { RecordError } from "./errors.js";
import type { FieldType, RecordDescriptor, RecordRow } from "./descriptor.js";
import { narrow, type RecordFilter } from "./query.js";

/** One figure, or one bar of a chart. `key` is null for an ungrouped total; otherwise the group
 * this figure is for. `value` is null when there was nothing to average. */
export interface AggregateBucket {
  readonly key: string | null;
  readonly value: number | null;
}

/** The answer to an aggregate request. */
export interface AggregateResult {
  readonly op: string;
  readonly buckets: readonly AggregateBucket[];
}

/** Grouping by the month a date falls in, rather than by the date itself — written as
 * `month_of:spent_on`, because "spend per month" is the question and "spend per day, then added
 * up by whoever is reading" is not. */
const monthPrefix = "month_of:";

/** The field types a sum, average or extremum runs over. */
const numericTypes: ReadonlySet<FieldType> = new Set(["integer", "decimal", "money"]);

function field<T extends RecordRow>(descriptor: RecordDescriptor<T>, fieldKey: string) {
  const target = descriptor.tryGetField(fieldKey);
  if (target === undefined)
    throw new RecordError("aggregate.field_unknown", `'${descriptor.entityKey}' has no field '${fieldKey}'.`);
  return target;
}

/** The numeric value a row contributes, or null when the field is blank — a row that never had a
 * value must not contribute a zero, because an average over ten rows where three are blank is an
 * average of seven. */
function numericValue(row: RecordRow, key: string): number | null {
  const value = row[key];
  if (value === null || value === undefined) return null;
  if (value instanceof Dec) return value.toNumber();
  if (typeof value === "number") return value;
  return null;
}

/** The month as `YYYY-MM` — the label a person reads, made here where formatting is cheap. */
function monthLabel(date: PlainDate): string {
  return `${String(date.year).padStart(4, "0")}-${String(date.month).padStart(2, "0")}`;
}

/**
 * Counting, summing and averaging rows — what a stat card and a chart both ask for.
 *
 * The rows arrive already narrowed by the same filters a list understands, so a chart and the
 * table under it always agree about which rows are in scope.
 */
export function aggregate<T extends RecordRow>(
  rows: readonly T[],
  descriptor: RecordDescriptor<T>,
  op: string,
  fieldKey: string | null,
  groupBy: string | null,
  filters: readonly RecordFilter[] = [],
): AggregateResult {
  if (op !== "count" && op !== "sum" && op !== "avg" && op !== "min" && op !== "max")
    throw new RecordError("aggregate.operation_unknown", `'${op}' is not an aggregate. Use count, sum, avg, min or max.`);

  if (op !== "count" && (fieldKey === null || fieldKey === ""))
    throw new RecordError("aggregate.field_required", `'${op}' needs a field to work on.`);

  if (op !== "count") {
    const target = field(descriptor, fieldKey!);
    if (!numericTypes.has(target.type))
      throw new RecordError(
        "aggregate.field_type",
        `'${target.key}' is not a number, so it cannot be summed or averaged.`,
      );
  }

  const narrowed = narrow(rows, descriptor, filters);

  if (groupBy === null || groupBy === "") {
    return { op, buckets: [{ key: null, value: total(narrowed, op, fieldKey) }] };
  }

  const month = groupBy.startsWith(monthPrefix);
  const groupField = month ? field(descriptor, groupBy.slice(monthPrefix.length)) : field(descriptor, groupBy);

  if (month && groupField.type !== "date" && groupField.type !== "datetime")
    throw new RecordError(
      "aggregate.group_type",
      `'${groupField.key}' is not a date, so records cannot be grouped by its month.`,
    );

  const groups = new Map<string, number[]>();
  for (const row of narrowed) {
    const raw = row[groupField.key];
    let key: string;

    if (month) {
      // A row with no date belongs to no month, so it is not in the chart.
      if (raw === null || raw === undefined) continue;
      const date = raw instanceof PlainDate ? raw : (raw as { utcDate(): PlainDate }).utcDate();
      key = monthLabel(date);
    } else {
      // The group key as text — a status, a category, a reference id. A blank key is the empty
      // group rather than a missing one, so the chart can show how many are unsorted.
      key = raw === null || raw === undefined ? "" : String(raw);
    }

    let bucket = groups.get(key);
    if (bucket === undefined) {
      bucket = [];
      groups.set(key, bucket);
    }
    bucket.push(op === "count" ? 0 : (numericValue(row, fieldKey!) ?? Number.NaN));
  }

  const buckets: AggregateBucket[] = [];
  for (const [key, values] of groups) {
    let value: number | null = null;
    if (op === "count") value = values.length;
    else {
      const present = values.filter((v) => !Number.isNaN(v));
      if (present.length > 0) {
        if (op === "sum") value = present.reduce((a, b) => a + b, 0);
        else if (op === "avg") value = present.reduce((a, b) => a + b, 0) / present.length;
        else if (op === "min") value = Math.min(...present);
        else value = Math.max(...present);
      }
    }
    buckets.push({ key, value });
  }

  buckets.sort((a, b) => (a.key! < b.key! ? -1 : a.key! > b.key! ? 1 : 0));
  return { op, buckets };
}

function total<T extends RecordRow>(rows: readonly T[], op: string, fieldKey: string | null): number | null {
  if (op === "count") return rows.length;

  const present = rows.map((row) => numericValue(row, fieldKey!)).filter((v): v is number => v !== null);
  if (present.length === 0) return null;
  if (op === "sum") return present.reduce((a, b) => a + b, 0);
  if (op === "avg") return present.reduce((a, b) => a + b, 0) / present.length;
  if (op === "min") return Math.min(...present);
  return Math.max(...present);
}
