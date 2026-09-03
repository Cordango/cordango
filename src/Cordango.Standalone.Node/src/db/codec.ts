// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See the repository root.

import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import type { RecordDescriptor, RecordRow } from "../records/descriptor.js";
import type { SqlRow } from "./driver.js";

/**
 * The bridge between kernel value objects and Postgres columns.
 *
 * Rows carry kernel objects, not raw JSON: `Dec` for decimal/money,
 * `Instant` for datetime, `PlainDate` for date, `string[]` for multiselect,
 * parsed JSON for json. The codec moves each one across the wire without
 * losing precision — the two traps being decimals (never through `number`)
 * and timestamps (never through JS `Date`, which truncates to milliseconds).
 */

/** Writes one kernel value into a parameter the driver accepts. */
export function encodeValue(value: unknown): unknown {
  if (value instanceof Dec) return value.toString();
  if (value instanceof Instant) return value.toString();
  if (value instanceof PlainDate) return value.toString();
  if (typeof value === "object" && value !== null && !(Array.isArray(value))) {
    return JSON.stringify(value);
  }
  return value;
}

/** Reads one column value back into the kernel value the row should carry. */
export function decodeValue(value: unknown, type: string): unknown {
  if (value === null || value === undefined) return null;

  switch (type) {
    case "integer":
      // Both drivers hand bigint columns back as JS BigInt; the row contract is number.
      return typeof value === "bigint" ? Number(value) : value;
    case "decimal":
    case "money":
      return typeof value === "string" ? Dec.parse(value) : null;
    case "datetime":
      return typeof value === "string" ? Instant.parse(value) : null;
    case "date":
      return typeof value === "string" ? PlainDate.parse(value) : null;
    case "json":
      return typeof value === "string" ? JSON.parse(value) : value;
    default:
      return value;
  }
}

/**
 * The SELECT list that reads every column losslessly. Timestamps and dates
 * come back as text via `to_json(col) #>> '{}'` — the unquote operator —
 * because both drivers otherwise hand back a JS `Date`, which is
 * millisecond-truncated. `#>> '{}'` returns the unquoted ISO-8601 string
 * (microsecond-faithful) for timestamptz and `yyyy-MM-dd` for date, and
 * works identically on PGlite and node-postgres.
 */
export function selectListSql<T extends RecordRow>(
  descriptor: RecordDescriptor<T>,
  hasTracking: boolean,
): string {
  const columns: string[] = [quoted("id")];

  for (const field of descriptor.fields) {
    const name = quoted(field.key);
    if (field.type === "datetime" || field.type === "date") {
      columns.push(`to_json(${name}) #>> '{}' AS ${name}`);
    } else {
      columns.push(name);
    }
  }

  if (hasTracking) {
    columns.push(`to_json(${quoted("created_at")}) #>> '{}' AS ${quoted("created_at")}`);
    columns.push(quoted("created_by"));
    columns.push(`to_json(${quoted("updated_at")}) #>> '{}' AS ${quoted("updated_at")}`);
    columns.push(quoted("updated_by"));
  }

  return columns.join(", ");
}

/** Quotes an identifier the way Postgres wants it. */
function quoted(name: string): string {
  return `"${name.replaceAll('"', '""')}"`;
}

/** Builds the parameter list for an INSERT/UPDATE of one row, in column order. */
export function encodeRow<T extends RecordRow>(
  row: T,
  descriptor: RecordDescriptor<T>,
  hasTracking: boolean,
): { columns: string[]; values: unknown[] } {
  const columns: string[] = [quoted("id")];
  const values: unknown[] = [row.id];

  for (const field of descriptor.fields) {
    columns.push(quoted(field.key));
    values.push(encodeValue(row[field.key]));
  }

  if (hasTracking) {
    const stamped = row as Record<string, unknown>;
    for (const key of ["created_at", "created_by", "updated_at", "updated_by"]) {
      columns.push(quoted(key));
      values.push(encodeValue(stamped[key]));
    }
  }

  return { columns, values };
}

/** Turns one raw SQL row into the kernel row the store hands upward. */
export function decodeRow<T extends RecordRow>(
  sqlRow: SqlRow,
  descriptor: RecordDescriptor<T>,
  hasTracking: boolean,
): T {
  const row = { id: String(sqlRow["id"]) } as T;
  const target = row as Record<string, unknown>;

  for (const field of descriptor.fields) {
    target[field.key] = decodeValue(sqlRow[field.key], field.type);
  }

  if (hasTracking) {
    target["created_at"] = decodeValue(sqlRow["created_at"], "datetime");
    target["created_by"] = sqlRow["created_by"] === null ? null : String(sqlRow["created_by"]);
    target["updated_at"] = decodeValue(sqlRow["updated_at"], "datetime");
    target["updated_by"] = sqlRow["updated_by"] === null ? null : String(sqlRow["updated_by"]);
  }

  return row;
}
