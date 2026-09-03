// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See the repository root.

import { RecordError } from "../records/errors.js";
import type { RecordDescriptor, RecordRow } from "../records/descriptor.js";
import type { Clock, RecordHooks } from "../records/hooks.js";
import type { CurrentUser } from "../security/permissions.js";
import { RecordStoreBase, type RecordIdGenerator } from "../records/store.js";
import { createTableSql } from "./ddl.js";
import { decodeRow, encodeRow, selectListSql } from "./codec.js";
import { isPgError, uniqueViolation, type SqlDriver } from "./driver.js";

/**
 * The database-backed store: the same hook/tracking pipeline as the
 * in-memory store, answering the four storage questions in SQL. The seam
 * (`RecordStoreApi.query()`) returns all rows unfiltered — filtering, sorting
 * and paging happen above, in the query layer — so this slice is plain
 * `SELECT *` with no WHERE translation.
 *
 * The table is created on demand; the first `cordango build` owns no
 * migration files, the runtime owns its own schema.
 */
export class SqlRecordStore<T extends RecordRow> extends RecordStoreBase<T> {
  private readonly tracking: boolean;

  constructor(
    descriptor: RecordDescriptor<T>,
    hooks: RecordHooks<T>,
    user: CurrentUser,
    clock: Clock,
    private readonly driver: SqlDriver,
    ids?: RecordIdGenerator,
    hasTracking?: boolean,
  ) {
    super(descriptor, hooks, user, clock, ids, hasTracking);
    this.tracking = hasTracking ?? true;
  }

  /** Creates the entity's table if it does not exist. Call once at boot. */
  async ensureTable(): Promise<void> {
    await this.driver.query(createTableSql(this.descriptor, this.tracking));
  }

  override async query(): Promise<T[]> {
    const sql =
      `SELECT ${selectListSql(this.descriptor, this.tracking)} FROM ${quoted(this.descriptor.entityKey)}`;
    const rows = await this.driver.query(sql);
    return rows.map((row) => decodeRow(row, this.descriptor, this.tracking));
  }

  protected override async exists(id: string): Promise<boolean> {
    const sql = `SELECT 1 FROM ${quoted(this.descriptor.entityKey)} WHERE ${quoted("id")} = $1`;
    const rows = await this.driver.query(sql, [id]);
    return rows.length > 0;
  }

  protected override async insert(row: T): Promise<void> {
    const { columns, values } = encodeRow(row, this.descriptor, this.tracking);
    const placeholders = values.map((_, index) => `$${index + 1}`).join(", ");
    const sql =
      `INSERT INTO ${quoted(this.descriptor.entityKey)} (${columns.join(", ")}) VALUES (${placeholders})`;

    try {
      await this.driver.query(sql, values);
    } catch (error) {
      if (isPgError(error, uniqueViolation)) {
        throw new RecordError(
          "record.duplicate_id",
          `A ${this.descriptor.label} with id '${row.id}' already exists.`,
          409,
        );
      }
      throw error;
    }
  }

  protected override async replace(row: T): Promise<void> {
    const { columns, values } = encodeRow(row, this.descriptor, this.tracking);
    const assignments = columns.slice(1).map((column, index) => `${column} = $${index + 2}`).join(", ");
    const sql =
      `UPDATE ${quoted(this.descriptor.entityKey)} SET ${assignments} WHERE ${quoted("id")} = $1`;

    const affected = await this.driver.execute(sql, [row.id, ...values.slice(1)]);
    if (affected === 0) {
      throw RecordError.notFound(this.descriptor.entityKey, row.id);
    }
  }

  protected override async remove(id: string): Promise<void> {
    const sql = `DELETE FROM ${quoted(this.descriptor.entityKey)} WHERE ${quoted("id")} = $1`;
    await this.driver.execute(sql, [id]);
  }
}

/** Quotes an identifier the way Postgres wants it. */
function quoted(name: string): string {
  return `"${name.replaceAll('"', '""')}"`;
}
