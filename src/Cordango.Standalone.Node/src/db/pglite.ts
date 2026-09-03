// SPDX-License-Identifier: Apache-2.0
// Copyright Cordango contributors

import { PGlite } from "@electric-sql/pglite";
import type { SqlDriver, SqlRow } from "./driver.js";

/**
 * In-process Postgres via PGlite. The default driver for standalone apps:
 * no server, no Docker, just a WASM Postgres living in the Node process.
 */
export class PgliteDriver implements SqlDriver {
  private readonly db: PGlite;

  constructor(db?: PGlite) {
    this.db = db ?? new PGlite();
  }

  async query(sql: string, params?: unknown[]): Promise<SqlRow[]> {
    const result = await this.db.query(sql, params ?? []);
    return result.rows as SqlRow[];
  }

  async execute(sql: string, params?: unknown[]): Promise<number> {
    const result = await this.db.query(sql, params ?? []);
    return result.affectedRows ?? 0;
  }

  async close(): Promise<void> {
    await this.db.close();
  }
}
