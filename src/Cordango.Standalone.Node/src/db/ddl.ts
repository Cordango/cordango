// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See the repository root.

import type { RecordDescriptor } from "../records/descriptor.js";

/**
 * The Postgres-dialect DDL emitter. Both drivers — PGlite in-process and
 * node-postgres against a server — speak the same dialect, so one emitter
 * serves both. The type mapping mirrors the .NET generator's `SchemaModel`
 * column-for-column, and the tracking columns carry the wire names the
 * runtime stamps (`created_at`/`created_by`/`updated_at`/`updated_by`).
 */

/** The Postgres column type for each Cord field type — parity with SchemaModel.Types(). */
export function columnType(type: string): string {
  switch (type) {
    case "id":
    case "reference":
      return "character varying(64)";
    case "integer":
      return "bigint";
    case "decimal":
      return "numeric";
    case "money":
      return "numeric(18,4)";
    case "boolean":
      return "boolean";
    case "date":
      return "date";
    case "datetime":
      return "timestamp with time zone";
    case "multiselect":
      return "text[]";
    case "json":
      return "jsonb";
    default:
      return "text";
  }
}

/** The tracking columns, in declaration order, when the entity carries them. */
export const trackingColumns: { name: string; type: string; notNull: boolean }[] = [
  { name: "created_at", type: "timestamp with time zone", notNull: true },
  { name: "created_by", type: "character varying(64)", notNull: false },
  { name: "updated_at", type: "timestamp with time zone", notNull: false },
  { name: "updated_by", type: "character varying(64)", notNull: false },
];

/** Quotes an identifier the way Postgres wants it. */
function quoted(name: string): string {
  return `"${name.replaceAll('"', '""')}"`;
}

/**
 * Emits `CREATE TABLE IF NOT EXISTS` for one entity: the id column, every
 * descriptor field, and — when the store tracks — the four tracking columns.
 */
export function createTableSql<T extends { id: string }>(
  descriptor: RecordDescriptor<T>,
  hasTracking: boolean,
): string {
  const columns: string[] = [`${quoted("id")} ${columnType("id")} NOT NULL`];

  for (const field of descriptor.fields) {
    columns.push(`${quoted(field.key)} ${columnType(field.type)}`);
  }

  if (hasTracking) {
    for (const column of trackingColumns) {
      columns.push(`${quoted(column.name)} ${column.type}${column.notNull ? " NOT NULL" : ""}`);
    }
  }

  columns.push(`PRIMARY KEY (${quoted("id")})`);

  return `CREATE TABLE IF NOT EXISTS ${quoted(descriptor.entityKey)} (\n  ${columns.join(",\n  ")}\n)`;
}
