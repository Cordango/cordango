// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
import type { SqlDriver } from "../db/driver.js";

/**
 * A credential a program can carry.
 *
 * **Why this exists at all.** Everything else about signing in here is a browser: a session cookie,
 * and an antiforgery token only a browser can be asked to echo. That is the right design for the
 * screens, and it locks out every caller that is not one — a script, a CI job, and the MCP
 * endpoint, which is a program acting for a person by definition.
 *
 * **It is not a record, deliberately.** A record gets a store, a gateway, a route and a place in
 * the tool surface. This table must have none of those: a caller who could list access keys
 * through the same door they came in by has found a way to enumerate every other credential in the
 * application. It is reachable only through {@link AccessKeyStore}, and every method there is
 * scoped to one owner.
 */
export interface AccessKey {
  /** The public half, which travels in the token in the clear. A lookup key and nothing else —
   * knowing it proves nothing. */
  id: string;
  /** What the owner called it, so a list of four keys is a list of four decisions rather than four
   * opaque strings. */
  label: string;
  /** The login this key acts as. A key is never more than its owner. */
  userId: string;
  created: Date;
  /** When it was last accepted. The one field that makes "which of these can I safely delete"
   * answerable. */
  lastUsed: Date | null;
  /** Optional. A key with no expiry is a key somebody has to remember to remove. */
  expires: Date | null;
}

/**
 * The token format.
 *
 * **Two halves, and only one of them is stored.** A token is `cordango_pat.<id>.<secret>`: the id
 * finds the row, the secret is compared against a hash. A single opaque string would mean either
 * scanning every row and hashing against each — a timing oracle that does not scale — or storing
 * something reversible.
 *
 * **SHA-256, not a password hash, and that is not an oversight.** Slow hashing exists to make
 * guessing a low-entropy human-chosen secret expensive. This secret is 32 bytes from the system
 * CSPRNG; there is nothing to guess, and a per-request key derivation would only make every
 * legitimate call slower.
 */
export const accessKeyPrefix = "cordango_pat";

/** The header a program presents its token in. */
export const bearerScheme = "Bearer";

const keyTable = `
CREATE TABLE IF NOT EXISTS "app_access_key" (
  "id" character varying(64) NOT NULL,
  "label" text NOT NULL DEFAULT '',
  "user_id" character varying(64) NOT NULL,
  "secret_hash" character varying(128) NOT NULL,
  "created" timestamp with time zone NOT NULL,
  "last_used" timestamp with time zone,
  "expires" timestamp with time zone,
  PRIMARY KEY ("id")
)`;

/** A fresh pair. The id is short because it is only a lookup; the secret is 32 bytes because it is
 * the whole of the security. */
export function mintToken(): { id: string; secret: string; token: string } {
  const id = randomBytes(8).toString("hex");
  const secret = randomBytes(32).toString("base64url");
  return { id, secret, token: `${accessKeyPrefix}.${id}.${secret}` };
}

/** Split a presented token. Shape only — whether it is valid is a question for the store, and
 * answering shape here keeps a malformed header from ever reaching the database. */
export function parseToken(token: string | undefined): { id: string; secret: string } | null {
  if (token === undefined || token === "") return null;

  const parts = token.split(".");
  if (parts.length !== 3 || parts[0] !== accessKeyPrefix) return null;

  const id = parts[1] ?? "";
  const secret = parts[2] ?? "";
  if (id === "" || secret === "") return null;

  return { id, secret };
}

function hashSecret(secret: string): string {
  return createHash("sha256").update(secret, "utf8").digest("hex");
}

type Row = Record<string, unknown>;

function text(value: unknown): string {
  return typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);
}

function date(value: unknown): Date | null {
  if (value instanceof Date) return value;
  if (typeof value === "string") {
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }
  return null;
}

/** Minting, listing, revoking and verifying — always for one owner. */
export class AccessKeyStore {
  constructor(private readonly driver: SqlDriver) {}

  async ensureTables(): Promise<void> {
    await this.driver.query(keyTable);
  }

  /** A new key. The returned token is the only time the secret exists outside the caller's hands. */
  async mint(
    userId: string,
    label: string,
    expires: Date | null,
  ): Promise<{ key: AccessKey; token: string }> {
    const minted = mintToken();
    const key: AccessKey = {
      id: minted.id,
      label: label.trim(),
      userId,
      created: new Date(),
      lastUsed: null,
      expires,
    };

    await this.driver.execute(
      `INSERT INTO "app_access_key" ("id","label","user_id","secret_hash","created","last_used","expires")
       VALUES ($1,$2,$3,$4,$5,$6,$7)`,
      [key.id, key.label, key.userId, hashSecret(minted.secret), key.created, null, key.expires],
    );

    return { key, token: minted.token };
  }

  /** This owner's keys, without anything secret in them. */
  async list(userId: string): Promise<AccessKey[]> {
    const rows = await this.driver.query(
      `SELECT "id","label","user_id","created","last_used","expires"
         FROM "app_access_key" WHERE "user_id" = $1 ORDER BY "created"`,
      [userId],
    );

    return rows.map((row) => ({
      id: text((row as Row)["id"]),
      label: text((row as Row)["label"]),
      userId: text((row as Row)["user_id"]),
      created: date((row as Row)["created"]) ?? new Date(0),
      lastUsed: date((row as Row)["last_used"]),
      expires: date((row as Row)["expires"]),
    }));
  }

  /** Delete one key, if it is this owner's. Answers false rather than throwing when it is not — a
   * caller probing for other people's key ids learns nothing either way. */
  async revoke(userId: string, keyId: string): Promise<boolean> {
    const affected = await this.driver.execute(
      `DELETE FROM "app_access_key" WHERE "id" = $1 AND "user_id" = $2`,
      [keyId, userId],
    );
    return affected > 0;
  }

  /** The login a token belongs to, or null if it is unknown, expired or wrong. */
  async verify(token: string | undefined): Promise<string | null> {
    const parsed = parseToken(token);
    if (parsed === null) return null;

    const rows = await this.driver.query(
      `SELECT "user_id","secret_hash","expires" FROM "app_access_key" WHERE "id" = $1`,
      [parsed.id],
    );
    const row = rows[0] as Row | undefined;
    if (row === undefined) return null;

    const expires = date(row["expires"]);
    if (expires !== null && expires.getTime() <= Date.now()) return null;

    const presented = Buffer.from(hashSecret(parsed.secret));
    const stored = Buffer.from(text(row["secret_hash"]));
    if (presented.length !== stored.length || !timingSafeEqual(presented, stored)) return null;

    // Best effort. A key that worked must not stop working because the bookkeeping write failed,
    // and `lastUsed` is a convenience for the owner's own list rather than part of the decision.
    try {
      await this.driver.execute(`UPDATE "app_access_key" SET "last_used" = $2 WHERE "id" = $1`, [
        parsed.id,
        new Date(),
      ]);
    } catch {
      // Deliberately swallowed, per the paragraph above.
    }

    return text(row["user_id"]);
  }

  /** Every key belonging to an account that is going away. Called when a login is deleted: a key
   * outliving its owner is a credential with nobody to revoke it. */
  async revokeAllFor(userId: string): Promise<void> {
    await this.driver.execute(`DELETE FROM "app_access_key" WHERE "user_id" = $1`, [userId]);
  }
}
