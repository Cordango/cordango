// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { readFile } from "node:fs/promises";
import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import type { RecordDescriptor, RecordRow } from "../records/descriptor.js";
import { SqlRecordStore } from "../db/sql-store.js";
import { RecordHooks } from "../records/hooks.js";
import { anonymous, type CordangoRuntime } from "./runtime.js";

/**
 * Loading the dataset a build produced.
 *
 * **Why an empty application is a bad first impression, and not only aesthetically.** A generated
 * application with no rows in it cannot be evaluated: every list is empty, every chart is blank,
 * every filter appears to work, and nothing tells you whether the thing you described was built
 * correctly.
 *
 * **The same seed file always produces the same rows.** Dates are stored as offsets from an anchor
 * the generator recorded — `{T-14}` is fourteen days before it — so a dataset built in March still
 * reads sensibly, with the same shape, whenever it is loaded. Setting `SEED_DATE` to `today`
 * re-anchors on the day it runs, which makes the data look current and makes the run
 * non-reproducible; that is a deliberate choice with a name rather than a default.
 *
 * **Rows go in without firing hooks.** Seeding is a bulk load of a dataset that was already
 * computed, and letting a workflow react to two hundred inserts would send two hundred
 * notifications about records nobody created.
 */
export async function runSeed(
  runtime: CordangoRuntime,
  path: string,
  dateMode?: string | null,
): Promise<number> {
  let text: string;
  try {
    text = await readFile(path, "utf8");
  } catch {
    console.log(`No seed file at ${path}; nothing to load.`);
    return 0;
  }

  let document: unknown;
  try {
    document = JSON.parse(text);
  } catch {
    console.warn(`The seed file at ${path} could not be read.`);
    return 0;
  }

  if (typeof document !== "object" || document === null) return 0;
  const parsed = document as { anchor?: unknown; entities?: unknown };

  const anchor = resolveAnchor(parsed.anchor, dateMode);
  const blocks = Array.isArray(parsed.entities) ? parsed.entities : [];

  let total = 0;

  // In file order, which is the order the generator worked out: an entity is written after
  // everything it points at, so a reference always has something to resolve against.
  for (const block of blocks) {
    if (typeof block !== "object" || block === null) continue;

    const { entity, rows } = block as { entity?: unknown; rows?: unknown };
    if (typeof entity !== "string") continue;

    const descriptor = runtime.descriptorFor(entity);
    if (descriptor === undefined) {
      console.warn(`The seed file has rows for '${entity}', which this application does not have.`);
      continue;
    }

    const added = await applyRows(runtime, descriptor, Array.isArray(rows) ? rows : [], anchor);
    total += added;
    if (added > 0) console.log(`Seeded ${added} ${entity} records.`);
  }

  console.log(
    total > 0
      ? `Seeding complete: ${total} records, anchored on ${anchor.toString()}.`
      : "Nothing seeded — the tables already have rows in them.",
  );

  return total;
}

async function applyRows(
  runtime: CordangoRuntime,
  descriptor: RecordDescriptor<RecordRow>,
  rows: readonly unknown[],
  anchor: PlainDate,
): Promise<number> {
  const store = new SqlRecordStore<RecordRow>(
    descriptor,
    // No hooks, deliberately — see the note on runSeed.
    new RecordHooks<RecordRow>([], [], [], [], [], []),
    anonymous,
    runtime.clock,
    runtime.driver,
  );

  // Only ever into an empty table. A seed run over live data would either duplicate the dataset or
  // overwrite somebody's work, and neither is what "load the demo data" means.
  if ((await store.query()).length > 0) return 0;

  let added = 0;

  for (const raw of rows) {
    if (typeof raw !== "object" || raw === null) continue;

    const row = typed(raw as Record<string, unknown>, descriptor, anchor);
    await store.create(row);
    added++;
  }

  return added;
}

/**
 * One seed row, with its values in the shapes the store writes.
 *
 * The seed file is JSON, so a money field arrives as a number and a date as a string. Converting
 * here rather than letting the codec guess is what keeps a seeded decimal from being stored as a
 * float that is very nearly the number the generator wrote.
 */
function typed(
  raw: Record<string, unknown>,
  descriptor: RecordDescriptor<RecordRow>,
  anchor: PlainDate,
): RecordRow {
  const row: RecordRow = { id: typeof raw["id"] === "string" ? raw["id"] : "" };

  for (const field of descriptor.fields) {
    const value = resolveOffsets(raw[field.key], anchor);
    if (value === undefined || value === null) {
      row[field.key] = null;
      continue;
    }

    switch (field.type) {
      case "integer":
        row[field.key] = typeof value === "number" ? Math.trunc(value) : Number(String(value)) || 0;
        break;
      case "decimal":
      case "money":
        row[field.key] = Dec.parse(String(value));
        break;
      case "boolean":
        row[field.key] = value === true || value === "true";
        break;
      case "date":
        row[field.key] = PlainDate.parse(String(value));
        break;
      case "datetime":
        row[field.key] = Instant.parse(String(value));
        break;
      case "multiselect":
        row[field.key] = Array.isArray(value) ? value.map((entry) => String(entry)) : [];
        break;
      case "json":
        row[field.key] = value;
        break;
      default:
        row[field.key] = String(value);
    }
  }

  return row;
}

const offset = /\{T([+-]\d+)?\}/;

/** Replace the date offsets in one value. `{T}` is the anchor, `{T-14}` and `{T+3}` are days either
 * side of it. */
function resolveOffsets(value: unknown, anchor: PlainDate): unknown {
  if (typeof value !== "string") return value;

  const match = offset.exec(value);
  if (match === null) return value;

  const days = match[1] === undefined ? 0 : Number(match[1]);
  const date = PlainDate.fromDayNumber(anchor.dayNumber + days).toString();

  // A datetime column wants a full instant; a date column wants a day. The token is the same either
  // way, so the SHAPE of the surrounding text decides — "{T-3}T09:00:00Z" keeps its time.
  return value.length === match[0].length ? date : value.replace(offset, date);
}

function resolveAnchor(recorded: unknown, dateMode?: string | null): PlainDate {
  const today = PlainDate.parse(new Date().toISOString().slice(0, 10));

  if (dateMode !== null && dateMode !== undefined && dateMode.toLowerCase() === "today")
    return today ?? new PlainDate(1970, 1, 1);

  const parsed = typeof recorded === "string" ? PlainDate.parse(recorded) : null;
  return parsed ?? today ?? new PlainDate(1970, 1, 1);
}
