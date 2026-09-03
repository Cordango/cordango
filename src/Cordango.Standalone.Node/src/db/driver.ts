// SPDX-License-Identifier: Apache-2.0
// Copyright Cordango contributors

/**
 * The SQL seam both PGlite (in-process) and node-postgres (server) satisfy.
 * Both speak the Postgres wire protocol, so one DDL emitter and one codec
 * serve both drivers.
 */
export interface SqlDriver {
  /** Runs a query with parameters and returns the resulting rows. */
  query(sql: string, params?: unknown[]): Promise<SqlRow[]>;
  /** Runs a statement that affects rows and returns the affected count. */
  execute(sql: string, params?: unknown[]): Promise<number>;
  /** Closes the underlying connection or pool. */
  close(): Promise<void>;
}

/** A single row of a result set, keyed by column name. */
export type SqlRow = { [column: string]: unknown };

/** Postgres unique-violation error code (SQLSTATE 23505). */
export const uniqueViolation = "23505";

/** Narrows an unknown thrown value to a Postgres error with a SQLSTATE code. */
export function isPgError(error: unknown, code?: string): boolean {
  if (typeof error !== "object" || error === null) return false;
  const e = error as { code?: unknown };
  if (typeof e.code !== "string") return false;
  return code === undefined || e.code === code;
}

/**
 * Opens a database connection. With a connection string (or a DATABASE_URL
 * environment variable) this is a node-postgres pool; without one it is an
 * in-process PGlite instance. Callers that want PGlite even when
 * DATABASE_URL is set should construct {@link PgliteDriver} directly.
 */
export async function openDatabase(connectionString?: string): Promise<SqlDriver> {
  const url = connectionString ?? process.env["DATABASE_URL"];
  if (url !== undefined && url !== "") {
    const { PostgresDriver } = await import("./postgres.js");
    return PostgresDriver.connect(url);
  }
  const { PgliteDriver } = await import("./pglite.js");
  return new PgliteDriver();
}
