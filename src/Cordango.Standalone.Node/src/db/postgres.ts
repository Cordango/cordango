// SPDX-License-Identifier: Apache-2.0
// Copyright Cordango contributors

import type { Pool, PoolClient, QueryResult } from "pg";
import type { SqlDriver, SqlRow } from "./driver.js";

/**
 * Server Postgres via node-postgres. Selected when a connection string is
 * available (explicit or DATABASE_URL). The `pg` package is an optional
 * peer dependency, imported dynamically so PGlite-only installs never load it.
 */
export class PostgresDriver implements SqlDriver {
  private readonly pool: Pool;

  private constructor(pool: Pool) {
    this.pool = pool;
  }

  static async connect(connectionString: string): Promise<PostgresDriver> {
    const pg = (await import("pg")) as typeof import("pg");
    return new PostgresDriver(new pg.Pool({ connectionString }));
  }

  async query(sql: string, params?: unknown[]): Promise<SqlRow[]> {
    const result = await this.pool.query(sql, params ?? []);
    return result.rows as SqlRow[];
  }

  async execute(sql: string, params?: unknown[]): Promise<number> {
    const result: QueryResult = await this.pool.query(sql, params ?? []);
    return result.rowCount ?? 0;
  }

  async close(): Promise<void> {
    await this.pool.end();
  }
}

// Referenced only to keep the pg types in the compile; the runtime import
// stays dynamic so the package remains optional.
export type { PoolClient };
