// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { SqlDriver } from "../db/driver.js";

/**
 * One person's saved layout for one table — which columns, in what order, how dense.
 *
 * **A convenience, and shaped like one.** Reading never fails loudly: having no saved layout is
 * the ordinary state of every table the first time anybody opens it. Writing is fire-and-forget
 * from the client's side, so a failed preference must never block a render or surface a message
 * about column widths.
 *
 * The payload is opaque to the server on purpose. What a saved layout contains is a decision the
 * table control owns, and a server that validated its shape would have to be redeployed every time
 * the control learned a new option.
 */
const table = `
CREATE TABLE IF NOT EXISTS "app_table_settings" (
  "user_id" character varying(64) NOT NULL,
  "handle" text NOT NULL,
  "table_key" text NOT NULL,
  "settings" jsonb NOT NULL,
  "updated_at" timestamp with time zone NOT NULL,
  PRIMARY KEY ("user_id", "handle", "table_key")
)`;

export class TableSettingsStore {
  constructor(private readonly driver: SqlDriver) {}

  async ensureTables(): Promise<void> {
    await this.driver.query(table);
  }

  /** This person's layout for one table, or null when they have never saved one. */
  async read(userId: string, handle: string, tableKey: string): Promise<unknown> {
    const rows = await this.driver.query(
      `SELECT "settings" FROM "app_table_settings"
        WHERE "user_id" = $1 AND "handle" = $2 AND "table_key" = $3`,
      [userId, handle, tableKey],
    );

    const value = (rows[0] as Record<string, unknown> | undefined)?.["settings"];
    if (value === undefined || value === null) return null;
    // PGlite hands back the parsed document; node-postgres hands back jsonb parsed too, but a
    // driver that returned text must not become a client-side parse error.
    if (typeof value === "string") {
      try {
        return JSON.parse(value);
      } catch {
        return null;
      }
    }
    return value;
  }

  /** Replace this person's layout for one table. Upserted rather than read-then-written: two tabs
   * saving at once should leave one of the two layouts, not a primary-key violation. */
  async write(userId: string, handle: string, tableKey: string, settings: unknown): Promise<void> {
    await this.driver.execute(
      `INSERT INTO "app_table_settings" ("user_id","handle","table_key","settings","updated_at")
       VALUES ($1,$2,$3,$4,$5)
       ON CONFLICT ("user_id","handle","table_key")
       DO UPDATE SET "settings" = EXCLUDED."settings", "updated_at" = EXCLUDED."updated_at"`,
      [userId, handle, tableKey, JSON.stringify(settings ?? {}), new Date()],
    );
  }

  /** Forget one. What the table control asks for when somebody resets a layout to the default. */
  async clear(userId: string, handle: string, tableKey: string): Promise<void> {
    await this.driver.execute(
      `DELETE FROM "app_table_settings"
        WHERE "user_id" = $1 AND "handle" = $2 AND "table_key" = $3`,
      [userId, handle, tableKey],
    );
  }
}
