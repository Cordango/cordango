// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { randomBytes, randomUUID } from "node:crypto";
import type { SqlDriver } from "../db/driver.js";
import { isPgError, uniqueViolation } from "../db/driver.js";
import { RecordError } from "../records/errors.js";
import { checkPassword, hashPassword, needsRehash, verifyPassword } from "./passwords.js";

/** The role name that is not one of the definition's roles. It is the runtime's own bypass: it
 * grants everything, everywhere, and no grant in any definition can name it. */
export const administratorRole = "Administrator";

/** Ten tries then a five-minute wait. Slow enough to make guessing pointless, short enough that
 * somebody who mistyped their own password is not locked out of their afternoon. */
const maxFailedAttempts = 10;
const lockoutMinutes = 5;

/** Identity's own idiom for "locked until somebody says otherwise", as a timestamp a database can
 * hold. */
const forever = new Date("9999-12-31T23:59:59.000Z");

/** Somebody who can sign in. */
export interface AppUser {
  id: string;
  email: string;
  /** Upper-cased, so a lookup is an index hit and two accounts cannot differ only by case. */
  normalizedEmail: string;
  displayName: string;
  /** The directory Person this login belongs to, when it belongs to one. A login and a person are
   * separate things: most people in the directory never sign in, and somebody leaving should lose
   * their login without erasing the approvals they signed. */
  personId: string | null;
  passwordHash: string | null;
  /**
   * Changes whenever the credential behind a session changes — a password reset, a lockout, a role
   * change. Every session cookie and every access key carries the stamp it was minted under, so
   * moving it kills all of them at once. A reset that leaves the old sessions alive is not a
   * reset; it is a second password.
   */
  securityStamp: string;
  /** This account is still on a password somebody else chose. Set when an administrator creates or
   * resets an account, cleared the moment the person changes it. */
  mustChangePassword: boolean;
  lockoutEnd: Date | null;
  failedAttempts: number;
  createdAt: Date;
  roles: string[];
}

/** What `GET /api/admin/users` answers with, and what the account endpoints describe. */
export interface UserSummary {
  id: string;
  email: string;
  displayName: string;
  personId: string | null;
  roles: string[];
  locked: boolean;
  mustChangePassword: boolean;
  createdAt: string;
}

const userTable = `
CREATE TABLE IF NOT EXISTS "app_user" (
  "id" character varying(64) NOT NULL,
  "email" text NOT NULL,
  "normalized_email" text NOT NULL,
  "display_name" text NOT NULL DEFAULT '',
  "person_id" character varying(64),
  "password_hash" text,
  "security_stamp" character varying(64) NOT NULL,
  "must_change_password" boolean NOT NULL DEFAULT false,
  "lockout_end" timestamp with time zone,
  "failed_attempts" integer NOT NULL DEFAULT 0,
  "created_at" timestamp with time zone NOT NULL,
  PRIMARY KEY ("id")
)`;

/** The uniqueness that makes "one account per address" a database fact rather than a check two
 * concurrent requests can both pass. */
const userIndex = `CREATE UNIQUE INDEX IF NOT EXISTS "ux_app_user_email" ON "app_user" ("normalized_email")`;

const roleTable = `
CREATE TABLE IF NOT EXISTS "app_user_role" (
  "user_id" character varying(64) NOT NULL,
  "role" text NOT NULL,
  PRIMARY KEY ("user_id", "role")
)`;

type Row = Record<string, unknown>;

function text(value: unknown): string {
  return typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);
}

function nullableText(value: unknown): string | null {
  const found = text(value);
  return found === "" ? null : found;
}

function date(value: unknown): Date | null {
  if (value instanceof Date) return value;
  if (typeof value === "string") {
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }
  return null;
}

/**
 * The sign-in tables, and every rule that protects them.
 *
 * Deliberately not an ORM and deliberately not a framework. What ASP.NET Core Identity gives the
 * .NET target — hashing, lockout, a security stamp, uniqueness — is about two hundred lines of SQL
 * and one hash function, and the alternative for Node is a dependency in the path of every login.
 * What it must NOT be is clever: every rule below has a counterpart in the .NET target, because
 * the two produce the same application.
 */
export class UserStore {
  constructor(private readonly driver: SqlDriver) {}

  /** Creates the tables if they are not there. Call once at boot, before anything serves. */
  async ensureTables(): Promise<void> {
    await this.driver.query(userTable);
    await this.driver.query(userIndex);
    await this.driver.query(roleTable);
  }

  /** Is there an administrator at all? The one question the first-run screen turns on. */
  async administratorExists(): Promise<boolean> {
    const rows = await this.driver.query(
      `SELECT 1 FROM "app_user_role" WHERE "role" = $1 LIMIT 1`,
      [administratorRole],
    );
    return rows.length > 0;
  }

  async findById(id: string): Promise<AppUser | null> {
    const rows = await this.driver.query(`SELECT * FROM "app_user" WHERE "id" = $1`, [id]);
    const row = rows[0];
    return row === undefined ? null : await this.hydrate(row as Row);
  }

  async findByEmail(email: string): Promise<AppUser | null> {
    const rows = await this.driver.query(
      `SELECT * FROM "app_user" WHERE "normalized_email" = $1`,
      [normalize(email)],
    );
    const row = rows[0];
    return row === undefined ? null : await this.hydrate(row as Row);
  }

  /** Every account, ordered by address so the admin list is stable between reloads. One query for
   * the users and one for the role links, rather than a round trip per account — which turns a page
   * of thirty into sixty-one queries. */
  async list(): Promise<AppUser[]> {
    const rows = await this.driver.query(`SELECT * FROM "app_user" ORDER BY "normalized_email"`);
    const links = await this.driver.query(`SELECT "user_id", "role" FROM "app_user_role"`);

    const byUser = new Map<string, string[]>();
    for (const link of links) {
      const userId = text((link as Row)["user_id"]);
      const role = text((link as Row)["role"]);
      const existing = byUser.get(userId);
      if (existing === undefined) byUser.set(userId, [role]);
      else existing.push(role);
    }

    return rows.map((row) => {
      const user = this.hydrateWithoutRoles(row as Row);
      user.roles = (byUser.get(user.id) ?? []).sort();
      return user;
    });
  }

  /** How many administrators there are, which is the number every "would this leave none?" check
   * is really asking about. */
  async administratorCount(): Promise<number> {
    const rows = await this.driver.query(
      `SELECT COUNT(*) AS "count" FROM "app_user_role" WHERE "role" = $1`,
      [administratorRole],
    );
    const value = (rows[0] as Row | undefined)?.["count"];
    return typeof value === "number" ? value : Number(text(value) || "0");
  }

  async create(input: {
    email: string;
    password: string;
    displayName?: string | undefined;
    personId?: string | null | undefined;
    roles?: readonly string[] | undefined;
    mustChangePassword?: boolean | undefined;
  }): Promise<AppUser> {
    const email = input.email.trim();
    const complaint = checkPassword(input.password);
    if (complaint !== null) throw new RecordError("auth.password_rejected", complaint, 400);
    if (!looksLikeEmail(email))
      throw new RecordError("user.rejected", `'${email}' is not a valid email address.`, 400);

    const displayName = (input.displayName ?? "").trim();

    const user: AppUser = {
      id: randomUUID().replaceAll("-", ""),
      email,
      normalizedEmail: normalize(email),
      displayName: displayName === "" ? email : displayName,
      personId: input.personId ?? null,
      passwordHash: await hashPassword(input.password),
      securityStamp: newStamp(),
      mustChangePassword: input.mustChangePassword ?? false,
      lockoutEnd: null,
      failedAttempts: 0,
      createdAt: new Date(),
      roles: [...(input.roles ?? [])],
    };

    try {
      await this.driver.execute(
        `INSERT INTO "app_user" ("id","email","normalized_email","display_name","person_id",
           "password_hash","security_stamp","must_change_password","lockout_end","failed_attempts","created_at")
         VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)`,
        [
          user.id, user.email, user.normalizedEmail, user.displayName, user.personId,
          user.passwordHash, user.securityStamp, user.mustChangePassword, user.lockoutEnd,
          user.failedAttempts, user.createdAt,
        ],
      );
    } catch (error) {
      if (isPgError(error, uniqueViolation))
        throw new RecordError("user.rejected", `An account for '${email}' already exists.`, 400);
      throw error;
    }

    // Written WITHOUT moving the stamp, which is the one difference from `setRoles`. The stamp
    // exists to kill credentials minted under an older authorisation, and an account created a
    // millisecond ago has none — moving it here would invalidate the session `/setup` is about to
    // mint for the administrator it just created, and sign them straight back out.
    await this.writeRoles(user.id, user.roles);
    return user;
  }

  /** The roles this account holds, replacing whatever it held. Written as a delete and an insert
   * rather than a diff: the set is tiny and a diff is a second place for the two to disagree. */
  async setRoles(userId: string, roles: readonly string[]): Promise<void> {
    await this.writeRoles(userId, roles);

    // A role change decides what a session may do, so the sessions minted before it must not
    // outlive it.
    await this.moveSecurityStamp(userId);
  }

  private async writeRoles(userId: string, roles: readonly string[]): Promise<void> {
    await this.driver.execute(`DELETE FROM "app_user_role" WHERE "user_id" = $1`, [userId]);
    for (const role of [...new Set(roles)].sort())
      await this.driver.execute(
        `INSERT INTO "app_user_role" ("user_id","role") VALUES ($1,$2)`,
        [userId, role],
      );
  }

  async updateProfile(
    userId: string,
    changes: { displayName?: string | undefined; personId?: string | null | undefined },
  ): Promise<void> {
    if (changes.displayName !== undefined)
      await this.driver.execute(`UPDATE "app_user" SET "display_name" = $2 WHERE "id" = $1`, [
        userId,
        changes.displayName.trim(),
      ]);

    if (changes.personId !== undefined)
      await this.driver.execute(`UPDATE "app_user" SET "person_id" = $2 WHERE "id" = $1`, [
        userId,
        changes.personId === null || changes.personId === "" ? null : changes.personId,
      ]);
  }

  /**
   * Set a password, moving the security stamp with it.
   *
   * The stamp is the reset. Writing the hash alone would leave every session and access key the
   * OLD password backed still working, which is not a reset but a second password.
   */
  async setPassword(userId: string, password: string, mustChange: boolean): Promise<void> {
    const complaint = checkPassword(password);
    if (complaint !== null) throw new RecordError("auth.password_rejected", complaint, 400);

    await this.driver.execute(
      `UPDATE "app_user"
         SET "password_hash" = $2, "security_stamp" = $3, "must_change_password" = $4,
             "failed_attempts" = 0, "lockout_end" = NULL
       WHERE "id" = $1`,
      [userId, await hashPassword(password), newStamp(), mustChange],
    );
  }

  /** Lock an account out, or let it back in. A lockout rather than a deletion, because deleting
   * somebody erases the account that created and approved things. */
  async setLocked(userId: string, locked: boolean): Promise<void> {
    await this.driver.execute(
      `UPDATE "app_user" SET "lockout_end" = $2, "failed_attempts" = 0 WHERE "id" = $1`,
      [userId, locked ? forever : null],
    );

    // A locked account's sessions and access keys die immediately rather than at their own expiry.
    // Locking somebody who is signed in and leaving their tab working is not locking them out.
    if (locked) await this.moveSecurityStamp(userId);
  }

  async delete(userId: string): Promise<void> {
    await this.driver.execute(`DELETE FROM "app_user_role" WHERE "user_id" = $1`, [userId]);
    await this.driver.execute(`DELETE FROM "app_user" WHERE "id" = $1`, [userId]);
  }

  /**
   * Check a password, counting the failures.
   *
   * One answer for "no such account" and for "wrong password", deliberately: two different answers
   * turn the login form into a way to find out who has an account here. The work is done even when
   * the account does not exist, so the two do not differ by how long they take either.
   */
  async checkCredentials(
    email: string,
    password: string,
  ): Promise<{ outcome: "ok"; user: AppUser } | { outcome: "invalid" } | { outcome: "locked" }> {
    const user = await this.findByEmail(email);

    if (user === null) {
      // A real hash, verified against a password that cannot match it. Without this the response
      // for an unknown address comes back in a millisecond and the response for a known one takes
      // the full scrypt cost, which is the same disclosure by a slower channel.
      await verifyPassword(password, decoyHash);
      return { outcome: "invalid" };
    }

    if (isLockedOut(user)) return { outcome: "locked" };

    if (!(await verifyPassword(password, user.passwordHash))) {
      const attempts = user.failedAttempts + 1;
      const locked = attempts >= maxFailedAttempts;
      await this.driver.execute(
        `UPDATE "app_user" SET "failed_attempts" = $2, "lockout_end" = $3 WHERE "id" = $1`,
        [
          user.id,
          locked ? 0 : attempts,
          locked ? new Date(Date.now() + lockoutMinutes * 60_000) : user.lockoutEnd,
        ],
      );
      return locked ? { outcome: "locked" } : { outcome: "invalid" };
    }

    if (user.failedAttempts !== 0 || user.lockoutEnd !== null)
      await this.driver.execute(
        `UPDATE "app_user" SET "failed_attempts" = 0, "lockout_end" = NULL WHERE "id" = $1`,
        [user.id],
      );

    // The cost was raised since this password was stored, and a successful sign-in is the one
    // moment the plaintext is in hand to write it again.
    if (needsRehash(user.passwordHash))
      await this.driver.execute(`UPDATE "app_user" SET "password_hash" = $2 WHERE "id" = $1`, [
        user.id,
        await hashPassword(password),
      ]);

    return { outcome: "ok", user };
  }

  private async moveSecurityStamp(userId: string): Promise<void> {
    await this.driver.execute(`UPDATE "app_user" SET "security_stamp" = $2 WHERE "id" = $1`, [
      userId,
      newStamp(),
    ]);
  }

  private async hydrate(row: Row): Promise<AppUser> {
    const user = this.hydrateWithoutRoles(row);
    const links = await this.driver.query(
      `SELECT "role" FROM "app_user_role" WHERE "user_id" = $1 ORDER BY "role"`,
      [user.id],
    );
    user.roles = links.map((link) => text((link as Row)["role"]));
    return user;
  }

  private hydrateWithoutRoles(row: Row): AppUser {
    return {
      id: text(row["id"]),
      email: text(row["email"]),
      normalizedEmail: text(row["normalized_email"]),
      displayName: text(row["display_name"]),
      personId: nullableText(row["person_id"]),
      passwordHash: nullableText(row["password_hash"]),
      securityStamp: text(row["security_stamp"]),
      mustChangePassword: row["must_change_password"] === true,
      lockoutEnd: date(row["lockout_end"]),
      failedAttempts: Number(text(row["failed_attempts"]) || "0"),
      createdAt: date(row["created_at"]) ?? new Date(0),
      roles: [],
    };
  }
}

/** True while the account is shut out — either by an administrator or by too many wrong
 * passwords. */
export function isLockedOut(user: AppUser): boolean {
  return user.lockoutEnd !== null && user.lockoutEnd.getTime() > Date.now();
}

/** The account as the admin list and the session endpoint describe it. Never the hash, never the
 * stamp: what comes back is enough to manage the account and nothing that could be used to be it. */
export function describeUser(user: AppUser): UserSummary {
  return {
    id: user.id,
    email: user.email,
    displayName: user.displayName === "" ? user.email : user.displayName,
    personId: user.personId,
    roles: [...user.roles].sort(),
    locked: isLockedOut(user),
    mustChangePassword: user.mustChangePassword,
    createdAt: user.createdAt.toISOString(),
  };
}

export function isAdministrator(user: AppUser): boolean {
  return user.roles.includes(administratorRole);
}

function normalize(email: string): string {
  return email.trim().toUpperCase();
}

function newStamp(): string {
  return randomBytes(16).toString("base64url");
}

function looksLikeEmail(value: string): boolean {
  // Deliberately loose. The only thing worth refusing here is something that is obviously not an
  // address; deciding which exotic addresses are real is the delivery system's job, not a regular
  // expression's.
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value);
}

/** A hash of a password nobody knows, so an unknown account costs the same as a known one. Built
 * once, because building it per request would itself be the timing signal. */
const decoyHash =
  "scrypt$32768$8$1$AAAAAAAAAAAAAAAAAAAAAA$"
  + "ZGVjb3ktbmV2ZXItbWF0Y2hlcy1hbnktcGFzc3dvcmQtdGhpcy1pcy1wYWRkaW5nLXRvLTY0LWJ5dGVzLi4uLg";
