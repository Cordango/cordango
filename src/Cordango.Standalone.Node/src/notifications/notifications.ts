// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { randomUUID } from "node:crypto";
import type { SqlDriver } from "../db/driver.js";

/**
 * Something that happened, addressed to one person.
 *
 * In the application rather than in an inbox: a definition's `notify` effect means "tell this
 * person", and telling them inside the thing they are already using is the version that needs no
 * mail server, no deliverability and no unsubscribe link.
 */
export interface Notification {
  id: string;
  /** The directory Person this is for. */
  person: string;
  title: string;
  message: string | null;
  /** Where to go when it is clicked — a record route, or nothing. */
  link: string | null;
  created: string;
  read_at: string | null;
}

const table = `
CREATE TABLE IF NOT EXISTS "notification" (
  "id" character varying(64) NOT NULL,
  "person" character varying(64) NOT NULL,
  "title" text NOT NULL,
  "message" text,
  "link" text,
  "created" timestamp with time zone NOT NULL,
  "read_at" timestamp with time zone,
  PRIMARY KEY ("id")
)`;

/** Every read of this table is "what is waiting for me", newest first. */
const index = `CREATE INDEX IF NOT EXISTS "ix_notification_person" ON "notification" ("person", "created")`;

type Row = Record<string, unknown>;

function text(value: unknown): string {
  return typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);
}

function nullableText(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return value.toISOString();
  return String(value);
}

function hydrate(row: Row): Notification {
  return {
    id: text(row["id"]),
    person: text(row["person"]),
    title: text(row["title"]),
    message: nullableText(row["message"]),
    link: nullableText(row["link"]),
    created: nullableText(row["created"]) ?? new Date(0).toISOString(),
    read_at: nullableText(row["read_at"]),
  };
}

/**
 * Writing one, and reading your own.
 *
 * Separate from the route because effects call it from inside a command, where there is no request
 * to read.
 */
export class NotificationService {
  constructor(private readonly driver: SqlDriver) {}

  async ensureTables(): Promise<void> {
    await this.driver.query(table);
    await this.driver.query(index);
  }

  /**
   * Tell somebody. Silently does nothing when there is nobody to tell.
   *
   * A `notify` whose recipient token resolved to nothing — an unassigned ticket, a claim with no
   * approver yet — is a notification with no addressee, and writing it would put a row in the table
   * that nobody will ever see and nobody can clear. The definition said "tell the assignee"; there
   * is no assignee; the honest outcome is silence.
   */
  async send(
    person: string | null | undefined,
    title: string,
    message: string | null,
    link: string | null,
  ): Promise<void> {
    if (person === null || person === undefined || person.trim() === "") return;
    if (title.trim() === "") return;

    await this.driver.execute(
      `INSERT INTO "notification" ("id","person","title","message","link","created","read_at")
       VALUES ($1,$2,$3,$4,$5,$6,NULL)`,
      [randomUUID().replaceAll("-", ""), person.trim(), title, message, link, new Date()],
    );
  }

  /** This person's notifications, newest first. Never anybody else's: the person is taken from the
   * session, never from the query string. */
  async list(person: string, take = 50): Promise<Notification[]> {
    const rows = await this.driver.query(
      `SELECT * FROM "notification" WHERE "person" = $1 ORDER BY "created" DESC LIMIT $2`,
      [person, Math.min(Math.max(take, 1), 200)],
    );
    return rows.map((row) => hydrate(row as Row));
  }

  async unreadCount(person: string): Promise<number> {
    const rows = await this.driver.query(
      `SELECT COUNT(*) AS "count" FROM "notification" WHERE "person" = $1 AND "read_at" IS NULL`,
      [person],
    );
    const value = (rows[0] as Row | undefined)?.["count"];
    return typeof value === "number" ? value : Number(text(value) || "0");
  }

  /** Mark one read, if it is this person's. The person is part of the WHERE rather than checked
   * afterwards, so marking somebody else's notification read is not expressible. */
  async markRead(person: string, id: string): Promise<boolean> {
    const affected = await this.driver.execute(
      `UPDATE "notification" SET "read_at" = $3 WHERE "id" = $1 AND "person" = $2 AND "read_at" IS NULL`,
      [id, person, new Date()],
    );
    return affected > 0;
  }

  async markAllRead(person: string): Promise<number> {
    return this.driver.execute(
      `UPDATE "notification" SET "read_at" = $2 WHERE "person" = $1 AND "read_at" IS NULL`,
      [person, new Date()],
    );
  }
}
