// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See the project root for details.

import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import type { AppPermissions, CurrentUser } from "../security/permissions.js";
import { resolveAccess } from "../security/permission-resolver.js";
import type { EntityAccess } from "../security/entity-access.js";
import { project, rejectedWrites } from "../security/record-visibility.js";
import { RecordError } from "./errors.js";
import type { FieldType, RecordDescriptor, RecordRow } from "./descriptor.js";
import type { RecordStoreApi } from "./store.js";
import { apply as applyQuery, narrow, type RecordFilter, type RecordSort } from "./query.js";
import { aggregate } from "./aggregate.js";

/** What a command answers with: the row as it ended up, and a line for the caller. */
export interface CommandResult {
  readonly record: RecordRow | null;
  readonly message: string | null;
}

/** The seam the command service plugs into. A command is not an update with a different name, so
 * the gateway does not guard it: the permission, the legality of the state transition and the
 * required input are all checked there, in that order and for the reason documented there. */
export interface CommandRunner<T extends RecordRow> {
  run(id: string, command: string, input: unknown, access: EntityAccess): Promise<CommandResult>;
}

/** One page of a list. */
export interface ListResult {
  readonly items: readonly RecordRow[];
  readonly total: number;
  readonly skip: number;
  readonly take: number;
}

/** A ceiling on `take`, so one request cannot ask for the whole table. */
export const maxPageSize = 500;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** A row value as the wire carries it: decimals as numbers, dates as their invariant text. */
function toWire(value: unknown): unknown {
  if (value instanceof Dec) return value.toJSON();
  if (value instanceof PlainDate) return value.toString();
  if (value instanceof Instant) return value.toString();
  if (Array.isArray(value)) return value.map(toWire);
  return value;
}

/** One row, with the fields this role may not read removed and every value in its wire shape. */
function projected(access: EntityAccess, row: RecordRow): RecordRow {
  const visible = project(access, row) ?? {};
  const result: RecordRow = { id: typeof visible["id"] === "string" ? visible["id"] : "" };
  for (const key of Object.keys(visible)) {
    if (key === "id") continue;
    result[key] = toWire(visible[key]);
  }
  return result;
}

/** The value of one field, checked and converted against the field's type. The message names the
 * offending property and says what was expected, which is the one piece of parse failure worth
 * putting on the wire. */
function fieldValue(key: string, type: FieldType, raw: unknown): unknown {
  if (raw === null) return null;

  switch (type) {
    case "text":
    case "reference":
    case "id":
      if (typeof raw !== "string") throw invalid(key, type);
      return raw;
    case "integer":
      if (typeof raw !== "number" || !Number.isInteger(raw)) throw invalid(key, type);
      return raw;
    case "decimal":
    case "money": {
      const value = typeof raw === "number" ? Dec.from(raw) : null;
      if (value === null) throw invalid(key, type);
      return value;
    }
    case "boolean":
      if (typeof raw !== "boolean") throw invalid(key, type);
      return raw;
    case "date": {
      const value = typeof raw === "string" ? PlainDate.parse(raw) : null;
      if (value === null) throw invalid(key, type);
      return value;
    }
    case "datetime": {
      const value = typeof raw === "string" ? Instant.parse(raw) : null;
      if (value === null) throw invalid(key, type);
      return value;
    }
    case "multiselect":
      if (!Array.isArray(raw) || raw.some((item) => typeof item !== "string")) throw invalid(key, type);
      return raw;
    case "json":
      return raw;
  }
}

function invalid(key: string, type: FieldType): RecordError {
  return new RecordError("request.body_invalid", `'${key}' is not a valid ${type}.`);
}

/** What a field holds when the body did not name it: the zero of its type, or null where the type
 * has no zero. Mirrors what deserialising a body into a typed record would leave behind. */
function defaultValue(type: FieldType): unknown {
  switch (type) {
    case "integer":
      return 0;
    case "decimal":
    case "money":
      return Dec.zero;
    case "boolean":
      return false;
    default:
      return null;
  }
}

/**
 * Every operation this application offers on one entity, with the caller's permissions applied,
 * and without knowing the entity's type.
 *
 * It exists so that a caller who is NOT a browser — an MCP client, a script, anything holding a
 * credential — reaches exactly what the same person reaches through the UI, resolved through the
 * same access rules and projected through the same visibility. Refusals are thrown as
 * {@link RecordError}, which the wire layer turns into the `{code, error}` body — so a refusal
 * reads identically whichever face asked.
 */
export class RecordGateway<T extends RecordRow> {
  private resolvedAccess: EntityAccess | undefined;

  constructor(
    private readonly store: RecordStoreApi<T>,
    private readonly permissions: AppPermissions,
    /** Who is asking. Named `caller` rather than `user` because two spellings of "the user"
     * differing in what they let you check is exactly the pair somebody gets wrong at three in
     * the morning. */
    readonly caller: CurrentUser,
    private readonly commands: CommandRunner<T>,
  ) {}

  get descriptor(): RecordDescriptor<T> {
    return this.store.descriptor;
  }

  /** The entity key as the definition spells it. */
  get entity(): string {
    return this.store.descriptor.entityKey;
  }

  /** The human label, for a message about this entity. */
  get label(): string {
    return this.store.descriptor.label;
  }

  /** Every field key this entity has, for a caller checking a payload before sending it. */
  get fieldKeys(): string[] {
    return this.store.descriptor.fieldKeys;
  }

  /** What this caller may do here. Resolved once and cached: it reads compiled-in data and
   * touches nothing else, but a request that lists and then writes should not ask twice and risk
   * two answers. */
  get access(): EntityAccess {
    return (this.resolvedAccess ??= this.resolveAccess());
  }

  /** Overridable because not everything a generated application serves came from the definition —
   * the built-in directory did not, and no role in the definition says anything about it. */
  protected resolveAccess(): EntityAccess {
    return resolveAccess(this.permissions, this.caller, this.entity);
  }

  async list(
    filters: readonly RecordFilter[],
    sort: readonly RecordSort[],
    skip: number,
    take: number,
  ): Promise<ListResult> {
    this.require(this.access.read, "read");

    const size = Math.min(Math.max(take, 1), maxPageSize);
    const offset = Math.max(skip, 0);

    const ordered = applyQuery(await this.store.query(), this.store.descriptor, filters, sort);

    // Counted before paging, so a caller can say "31 of 214" rather than "31 of the 31 you can
    // see".
    const total = ordered.length;
    const rows = ordered.slice(offset, offset + size);

    return { items: rows.map((row) => projected(this.access, row)), total, skip: offset, take: size };
  }

  async aggregateRows(
    op: string,
    field: string | null,
    groupBy: string | null,
    filters: readonly RecordFilter[],
  ): Promise<{ op: string; buckets: readonly { key: string | null; value: number | null }[] }> {
    this.require(this.access.read, "read");

    // A field this role cannot read cannot be summed either. Without this, a total over salary
    // would tell somebody the payroll of a column they may not see — an aggregate is a slower way
    // of reading a field, not a different kind of access.
    if (field !== null && !this.access.canReadField(field))
      throw RecordError.forbidden("record.read_denied", `Your role may not read ${field}.`);

    if (groupBy !== null) {
      const grouped = groupBy.startsWith("month_of:") ? groupBy.slice("month_of:".length) : groupBy;
      if (!this.access.canReadField(grouped))
        throw RecordError.forbidden("record.read_denied", `Your role may not read ${grouped}.`);
    }

    const rows = await this.store.query();
    const narrowed = narrow(rows, this.store.descriptor, filters);
    return aggregate(narrowed, this.store.descriptor, op, field, groupBy);
  }

  async get(id: string): Promise<RecordRow> {
    this.require(this.access.read, "read");

    const record = await this.store.find(id);
    if (record === undefined) throw RecordError.notFound(this.entity, id);
    return projected(this.access, record);
  }

  async create(body: unknown): Promise<RecordRow> {
    this.require(this.access.create, "create");

    const { record, supplied } = this.read(body);
    this.refuse(supplied);

    return projected(this.access, await this.store.create(record));
  }

  /** Write the fields named by `fields`. Pass {@link fieldKeys} for a replace and the body's own
   * keys for a patch — the difference between the two verbs is entirely in this argument. */
  async write(id: string, body: unknown, fields: readonly string[]): Promise<RecordRow> {
    this.require(this.access.update, "update");

    const { record, supplied } = this.read(body);

    // Checked against what the CALLER SENT, not against the field list being written. A replace
    // writes every field by definition, and refusing it because the payload happened to include
    // one the role may not set is right; refusing it because the entity HAS such a field would
    // make replace impossible for that role rather than merely restricted.
    this.refuse(supplied);

    return projected(this.access, await this.store.update(id, record, fields));
  }

  async delete(id: string): Promise<void> {
    this.require(this.access.delete_, "delete");
    await this.store.delete(id);
  }

  runCommand(id: string, command: string, input: unknown): Promise<CommandResult> {
    return this.commands.run(id, command, input, this.access);
  }

  /** The keys a body actually named. Needed because "absent" and "explicitly null" are different
   * requests and a parsed row cannot tell them apart. */
  suppliedKeys(body: unknown): string[] {
    return isPlainObject(body) ? Object.keys(body) : [];
  }

  /** 403 rather than 404. Hiding an entity's existence from somebody who already knows its route
   * buys nothing here — every route is in the application's own generated client. */
  private require(allowed: boolean, operation: string): void {
    if (allowed) return;
    throw RecordError.forbidden(`record.${operation}_denied`, `Your role may not ${operation} ${this.label}.`);
  }

  private read(body: unknown): { record: T; supplied: string[] } {
    if (!isPlainObject(body)) throw new RecordError("request.body_invalid", "Expected a JSON object.");

    // Every field starts at its type's default, as deserialisation into a typed record would give
    // it: a replace writes every field, and one the body left out must land as a zero or a null
    // rather than as an undefined that erases the key.
    const record = { id: typeof body["id"] === "string" ? body["id"] : "" } as T;
    const target = record as Record<string, unknown>;
    for (const field of this.store.descriptor.fields) {
      if (field.key === "id") continue;
      target[field.key] = defaultValue(field.type);
    }
    for (const field of this.store.descriptor.fields) {
      if (!(field.key in body)) continue;
      target[field.key] = fieldValue(field.key, field.type, body[field.key]);
    }

    return { record, supplied: this.suppliedKeys(body) };
  }

  /** Refuse a write that touches fields this role may not set — all of them at once, so a form
   * marks every offending field rather than one per attempt. */
  private refuse(supplied: readonly string[]): void {
    const rejected = rejectedWrites(this.access, supplied);
    if (rejected.length > 0) throw RecordError.writeRestricted(rejected);
  }
}
