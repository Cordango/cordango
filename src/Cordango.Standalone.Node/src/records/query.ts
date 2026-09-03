// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See the project root for details.

import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import { RecordError } from "./errors.js";
import type { FieldType, RecordDescriptor, RecordRow } from "./descriptor.js";

/** One condition a list is narrowed by. The value stays text — only the field it is compared
 * against knows what "3" means. */
export interface RecordFilter {
  readonly field: string;
  readonly operator: string;
  readonly value: string | null;
}

/** Which field to order by, and which way. */
export interface RecordSort {
  readonly field: string;
  readonly descending: boolean;
}

/** The field types whose values are plain strings — what `contains` and `startsWith` require. */
const textTypes: ReadonlySet<FieldType> = new Set(["text", "reference", "id"]);

/** The field types a sum, average or extremum runs over. */
const numericTypes: ReadonlySet<FieldType> = new Set(["integer", "decimal", "money"]);

function isBlank(value: unknown): boolean {
  return value === null || value === undefined;
}

/** The comparison value, typed against the field it is compared with. */
function constant(raw: string | null, type: FieldType): unknown {
  if (raw === null) return null;
  if (type === "boolean") return raw === "true" || raw === "1" || raw === "yes";
  if (type === "integer") {
    if (!/^[+-]?\d+$/.test(raw.trim())) throw invalid(raw, "Int64");
    return Number(raw);
  }
  if (type === "decimal" || type === "money") {
    const value = Dec.parse(raw);
    if (value === null) throw invalid(raw, "Decimal");
    return value;
  }
  if (type === "date") {
    const value = PlainDate.parse(raw);
    if (value === null) throw invalid(raw, "DateOnly");
    return value;
  }
  if (type === "datetime") {
    const value = Instant.parse(raw);
    if (value === null) throw invalid(raw, "DateTimeOffset");
    return value;
  }
  return raw;
}

function invalid(raw: string, typeName: string): RecordError {
  return new RecordError("query.value_invalid", `'${raw}' is not a valid ${typeName}.`);
}

/** Ordinal comparison of two typed values, or null when they cannot be ordered. */
function compare(left: unknown, right: unknown): number | null {
  if (left instanceof Dec && right instanceof Dec) return left.cmp(right);
  if (left instanceof PlainDate && right instanceof PlainDate) return left.cmp(right);
  if (left instanceof Instant && right instanceof Instant) return left.cmp(right);
  if (typeof left === "number" && typeof right === "number")
    return left < right ? -1 : left > right ? 1 : 0;
  if (typeof left === "string" && typeof right === "string")
    return left < right ? -1 : left > right ? 1 : 0;
  if (typeof left === "boolean" && typeof right === "boolean")
    return left === right ? 0 : left ? 1 : -1;
  return null;
}

function equals(left: unknown, right: unknown): boolean {
  if (left instanceof Dec) return right instanceof Dec && left.eq(right);
  if (left instanceof PlainDate) return right instanceof PlainDate && left.cmp(right) === 0;
  if (left instanceof Instant) return right instanceof Instant && left.cmp(right) === 0;
  return left === right;
}

/** Empty means null, and for text it also means the empty string — a field somebody cleared in a
 * form and a field nobody ever filled are the same thing to the person asking. */
function isEmpty(value: unknown): boolean {
  return isBlank(value) || value === "";
}

function field<T extends RecordRow>(descriptor: RecordDescriptor<T>, fieldKey: string) {
  const field = descriptor.tryGetField(fieldKey);
  if (field === undefined)
    throw new RecordError("query.field_unknown", `'${descriptor.entityKey}' has no field '${fieldKey}'.`);
  return field;
}

function matches<T extends RecordRow>(row: T, descriptor: RecordDescriptor<T>, filter: RecordFilter): boolean {
  const target = field(descriptor, filter.field);
  const value = row[target.key];

  // The absence tests come first: they are the two operators that do not need a value.
  if (filter.operator === "isEmpty") return isEmpty(value);
  if (filter.operator === "isNotEmpty") return !isEmpty(value);

  if (filter.operator === "in" || filter.operator === "notIn") {
    const values = (filter.value ?? "")
      .split("|")
      .filter((term) => term !== "")
      .map((term) => constant(term, target.type));

    // "in nothing" matches nothing, and "not in nothing" matches everything. Falling through to a
    // comparison over an empty list would silently do the opposite of both.
    if (values.length === 0) return filter.operator === "notIn";
    if (isBlank(value)) return false;
    const any = values.some((candidate) => equals(value, candidate));
    return filter.operator === "in" ? any : !any;
  }

  // A closed range, both ends included, written as one leaf because that is how the language
  // spells it.
  if (filter.operator === "between") {
    const bounds = (filter.value ?? "").split("|");
    if (bounds.length !== 2 || bounds.some((bound) => bound.trim() === ""))
      throw new RecordError(
        "query.range_invalid",
        `'between' needs both bounds, written lo|hi. '${filter.field}' was given '${filter.value}'.`,
      );

    if (isBlank(value)) return false;
    const lo = compare(value, constant(bounds[0] ?? "", target.type));
    const hi = compare(value, constant(bounds[1] ?? "", target.type));
    return lo !== null && hi !== null && lo >= 0 && hi <= 0;
  }

  if (filter.operator === "contains" || filter.operator === "startsWith") {
    if (!textTypes.has(target.type))
      throw new RecordError(
        "query.operator_type",
        `'${filter.operator}' compares text, and '${filter.field}' is not text.`,
      );

    // A null column is not a match.
    if (typeof value !== "string") return false;
    return filter.operator === "contains"
      ? value.includes(filter.value ?? "")
      : value.startsWith(filter.value ?? "");
  }

  const expected = constant(filter.value, target.type);
  switch (filter.operator) {
    case "eq":
      return isBlank(value) ? isBlank(expected) : equals(value, expected);
    case "neq":
      return isBlank(value) ? !isBlank(expected) : !equals(value, expected);
    case "gt":
    case "gte":
    case "lt":
    case "lte": {
      if (isBlank(value) || isBlank(expected)) return false;
      const order = compare(value, expected);
      if (order === null) return false;
      if (filter.operator === "gt") return order > 0;
      if (filter.operator === "gte") return order >= 0;
      if (filter.operator === "lt") return order < 0;
      return order <= 0;
    }
    default:
      throw new RecordError(
        "query.operator_unknown",
        `'${filter.operator}' is not a filter operator. Use eq, neq, gt, gte, lt, lte, between, in, notIn, contains, startsWith, isEmpty or isNotEmpty.`,
      );
  }
}

/** Sort comparison treating a blank as greater than any value, so blanks sit at the end of an
 * ascending page — where a person looking for what is missing expects to find them. */
function compareForSort(left: unknown, right: unknown): number {
  if (isBlank(left) && isBlank(right)) return 0;
  if (isBlank(left)) return 1;
  if (isBlank(right)) return -1;
  const order = compare(left, right);
  if (order !== null) return order;
  const leftText = String(left);
  const rightText = String(right);
  return leftText < rightText ? -1 : leftText > rightText ? 1 : 0;
}

/**
 * Turning a list request into rows: filter, then order. The ordering always ends on the id, so a
 * page is only meaningful over a total order — sorting by a status half the rows share would
 * otherwise leave those rows free to shuffle between pages, and a row can appear twice while
 * another never does.
 */
export function apply<T extends RecordRow>(
  rows: readonly T[],
  descriptor: RecordDescriptor<T>,
  filters: readonly RecordFilter[],
  sort: readonly RecordSort[],
): T[] {
  const narrowed = narrow(rows, descriptor, filters);
  const result = [...narrowed];

  // Sorted fields are looked up, never assumed — the descriptor is the allowlist.
  for (const term of sort) field(descriptor, term.field);

  result.sort((a, b) => {
    for (const term of sort) {
      const order = compareForSort(a[term.field], b[term.field]);
      if (order !== 0) return term.descending ? -order : order;
    }
    return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
  });

  return result;
}

/** Narrow, without ordering. An aggregate groups rather than pages, so the total order a list
 * needs is only noise. */
export function narrow<T extends RecordRow>(
  rows: readonly T[],
  descriptor: RecordDescriptor<T>,
  filters: readonly RecordFilter[],
): T[] {
  return rows.filter((row) => filters.every((filter) => matches(row, descriptor, filter)));
}

/**
 * Parse the query string's filter terms: `field:operator:value`. Split into three at most, so a
 * value containing a colon — a time, a URL — survives intact.
 */
export function parseFilters(terms: Iterable<string | null | undefined> | null): RecordFilter[] {
  if (terms === null) return [];

  const filters: RecordFilter[] = [];
  for (const term of terms) {
    if (term === null || term === undefined || term.trim() === "") continue;

    const parts = term.split(":", 3);
    if (parts.length >= 3)
      filters.push({ field: parts[0]!, operator: parts[1]!, value: parts[2]! });
    else if (parts.length === 2)
      filters.push({ field: parts[0]!, operator: parts[1]!, value: null });
    else
      throw new RecordError(
        "query.filter_invalid",
        `'${term}' is not a filter. Write field:operator:value, for example status:eq:open.`,
      );
  }

  return filters;
}

/** Parse the sort terms: `field` ascending, `-field` descending. */
export function parseSort(sort: string | null | undefined): RecordSort[] {
  if (sort === null || sort === undefined || sort.trim() === "") return [];

  return sort
    .split(",")
    .map((term) => term.trim())
    .filter((term) => term !== "")
    .map((term) =>
      term.startsWith("-") ? { field: term.slice(1), descending: true } : { field: term, descending: false },
    );
}
