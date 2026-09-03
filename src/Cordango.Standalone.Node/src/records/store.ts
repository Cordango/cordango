// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See the repository root.

import type { CurrentUser } from "../security/permissions.js";
import { RecordError } from "./errors.js";
import type { RecordDescriptor, RecordRow } from "./descriptor.js";
import type { Clock, RecordContext, RecordHooks } from "./hooks.js";

/** Where a new record's id comes from when the client did not supply one. */
export interface RecordIdGenerator {
  newId(): string;
}

/** The default: a compact uuid. An application that wants readable keys — invoice numbers, slugs —
 * replaces this registration with its own. */
export class GuidRecordIdGenerator implements RecordIdGenerator {
  newId(): string {
    const crypto = (globalThis as unknown as { crypto: { randomUUID(): string } }).crypto;
    return crypto.randomUUID().replaceAll("-", "");
  }
}

/** Reading and writing one entity's rows, hooks included — the seam the database layer plugs into. */
export interface RecordStoreApi<T extends RecordRow> {
  readonly descriptor: RecordDescriptor<T>;

  /** Unfiltered, for the caller to filter, sort and page. */
  query(): Promise<T[]>;

  find(id: string): Promise<T | undefined>;

  create(record: T): Promise<T>;

  /** Apply `fieldKeys` of `incoming` to the stored row. Only the named fields move, so a client
   * that sends three fields does not blank the other twenty. */
  update(id: string, incoming: T, fieldKeys: Iterable<string>): Promise<T>;

  delete(id: string): Promise<void>;
}

function isUnset(value: unknown): boolean {
  return value === null || value === undefined;
}

/**
 * The pipeline every store shares: id generation, hooks, and tracking stamps, in the order the
 * in-memory store established. A subclass answers the four storage questions — does this id exist,
 * insert, read back, replace — and everything above it is storage-agnostic.
 *
 * Tracking fields are not descriptor fields — the descriptor carries what was authored, and the
 * store stamps `created_at`/`created_by`/`updated_at`/`updated_by` by their wire keys, only when the
 * entity carries them at all.
 */
export abstract class RecordStoreBase<T extends RecordRow> implements RecordStoreApi<T> {
  private readonly context: RecordContext;

  constructor(
    readonly descriptor: RecordDescriptor<T>,
    private readonly hooks: RecordHooks<T>,
    private readonly user: CurrentUser,
    private readonly clock: Clock,
    private readonly ids: RecordIdGenerator = new GuidRecordIdGenerator(),
    private readonly hasTracking = true,
  ) {
    this.context = { user, clock };
  }

  abstract query(): Promise<T[]>;
  protected abstract exists(id: string): Promise<boolean>;
  protected abstract insert(row: T): Promise<void>;
  protected abstract replace(row: T): Promise<void>;
  protected abstract remove(id: string): Promise<void>;

  async find(id: string): Promise<T | undefined> {
    return (await this.query()).find((row) => row.id === id);
  }

  async create(record: T): Promise<T> {
    // A client may choose the id — handles like "eur" are legitimate keys. It just may not choose
    // one that is taken.
    if (record.id.trim() === "") record.id = this.ids.newId();
    else if (await this.exists(record.id))
      throw new RecordError(
        "record.duplicate_id",
        `A ${this.descriptor.label} with id '${record.id}' already exists.`,
        409,
      );

    await this.hooks.beforeCreate(record, this.context);

    if (this.hasTracking) {
      const stamped = record as Record<string, unknown>;
      if (isUnset(stamped["created_at"])) stamped["created_at"] = this.clock.utcNow;
      if (isUnset(stamped["created_by"])) stamped["created_by"] = this.user.userId;
    }

    await this.insert(record);
    await this.hooks.afterCreate(record, this.context);
    return record;
  }

  async update(id: string, incoming: T, fieldKeys: Iterable<string>): Promise<T> {
    const stored = await this.find(id);
    if (stored === undefined) throw RecordError.notFound(this.descriptor.entityKey, id);

    // Taken before anything is applied: once the new values are on the row, the old ones are gone
    // and "which fields changed" can no longer be asked.
    const before = this.descriptor.copy(stored);

    this.descriptor.apply(incoming, stored, fieldKeys);

    // The id is not a field and is never moved by Apply. Changing a row's identity through an
    // update would orphan every reference pointing at it.
    stored.id = id;

    await this.hooks.beforeUpdate(stored, before, this.context);

    if (this.hasTracking) {
      const stamped = stored as Record<string, unknown>;
      stamped["updated_at"] = this.clock.utcNow;
      stamped["updated_by"] = this.user.userId;
    }

    await this.replace(stored);
    await this.hooks.afterUpdate(stored, before, this.context);
    return stored;
  }

  async delete(id: string): Promise<void> {
    const stored = await this.find(id);
    if (stored === undefined) throw RecordError.notFound(this.descriptor.entityKey, id);

    await this.hooks.beforeDelete(stored, this.context);
    await this.remove(id);
    await this.hooks.afterDelete(stored, this.context);
  }
}

/**
 * The in-memory store: rows in a Map, tracking stamped on the row itself. The database-backed
 * store implements the same interface; everything above it is storage-agnostic.
 */
export class RecordStore<T extends RecordRow> extends RecordStoreBase<T> {
  private readonly rows = new Map<string, T>();

  constructor(
    descriptor: RecordDescriptor<T>,
    hooks: RecordHooks<T>,
    user: CurrentUser,
    clock: Clock,
    ids?: RecordIdGenerator,
    hasTracking?: boolean,
  ) {
    super(descriptor, hooks, user, clock, ids, hasTracking);
  }

  override async query(): Promise<T[]> {
    return [...this.rows.values()];
  }

  protected override async exists(id: string): Promise<boolean> {
    return this.rows.has(id);
  }

  protected override async insert(row: T): Promise<void> {
    this.rows.set(row.id, row);
  }

  protected override async replace(row: T): Promise<void> {
    this.rows.set(row.id, row);
  }

  protected override async remove(id: string): Promise<void> {
    this.rows.delete(id);
  }
}
