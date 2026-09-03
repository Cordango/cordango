// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See the repository root.

import { afterAll, beforeAll, describe, expect, it } from "vitest";
import {
  Dec,
  Instant,
  PlainDate,
  PgliteDriver,
  RecordDescriptor,
  RecordError,
  RecordHooks,
  SqlRecordStore,
  type Clock,
  type CurrentUser,
  type RecordRow,
} from "../src/index.js";

type Everything = RecordRow & {
  name: string | null;
  count: number | null;
  price: Dec | null;
  paid: Dec | null;
  active: boolean | null;
  born: PlainDate | null;
  seen: Instant | null;
  parent: string | null;
  tags: string[] | null;
  extra: Record<string, unknown> | null;
};

class FixedClock implements Clock {
  constructor(public utcNow: Instant) {}
}

class FakeUser implements CurrentUser {
  constructor(
    public userId: string | null,
    public isAdministrator = false,
    public roleKeys: readonly string[] = [],
  ) {}
}

const descriptor = new RecordDescriptor<Everything>("everything", "Everything", [
  { key: "name", type: "text" },
  { key: "count", type: "integer" },
  { key: "price", type: "decimal" },
  { key: "paid", type: "money" },
  { key: "active", type: "boolean" },
  { key: "born", type: "date" },
  { key: "seen", type: "datetime" },
  { key: "parent", type: "reference" },
  { key: "tags", type: "multiselect" },
  { key: "extra", type: "json" },
]);

const noHooks = new RecordHooks<Everything>([], [], [], [], [], []);

let driver: PgliteDriver;

beforeAll(async () => {
  driver = new PgliteDriver();
});

afterAll(async () => {
  await driver.close();
});

function store(clock?: FixedClock, user?: FakeUser): SqlRecordStore<Everything> {
  return new SqlRecordStore(
    descriptor,
    noHooks,
    user ?? new FakeUser("someone"),
    clock ?? new FixedClock(Instant.parse("2026-01-02T03:04:05Z")!),
    driver,
  );
}

function row(init: Partial<Everything> = {}): Everything {
  return {
    id: "",
    name: null,
    count: null,
    price: null,
    paid: null,
    active: null,
    born: null,
    seen: null,
    parent: null,
    tags: null,
    extra: null,
    ...init,
  };
}

describe("SqlRecordStore", () => {
  it("creates the table and round-trips every field type", async () => {
    const world = store();
    await world.ensureTable();

    const micros = Instant.parse("2026-09-03T13:34:56.789123+01:00")!;
    const created = await world.create(
      row({
        name: "first",
        count: 9007199254740991,
        price: Dec.parse("-12345.6789"),
        paid: Dec.parse("0.0001"),
        active: true,
        born: PlainDate.parse("0001-01-01"),
        seen: micros,
        parent: "root",
        tags: ["a", "b"],
        extra: { nested: { deep: [1, 2, 3] } },
      }),
    );

    expect(created.id).not.toBe("");

    const read = await world.find(created.id);
    expect(read).toBeDefined();
    expect(read!["name"]).toBe("first");
    expect(read!["count"]).toBe(9007199254740991);
    expect((read!["price"] as Dec).toString()).toBe("-12345.6789");
    expect((read!["paid"] as Dec).toString()).toBe("0.0001");
    expect(read!["active"]).toBe(true);
    expect((read!["born"] as PlainDate).toString()).toBe("0001-01-01");
    expect((read!["seen"] as Instant).epochMicros).toBe(micros.epochMicros);
    expect(read!["parent"]).toBe("root");
    expect(read!["tags"]).toEqual(["a", "b"]);
    expect(read!["extra"]).toEqual({ nested: { deep: [1, 2, 3] } });
  });

  it("nulls survive the round trip", async () => {
    const world = store();
    await world.ensureTable();

    const created = await world.create(row({ name: "empty" }));
    const read = await world.find(created.id);

    for (const key of ["count", "price", "paid", "active", "born", "seen", "parent", "tags", "extra"]) {
      expect(read![key]).toBeNull();
    }
  });

  it("tracking is stamped and survives the round trip", async () => {
    const clock = new FixedClock(Instant.parse("2026-01-02T03:04:05.123456Z")!);
    const world = store(clock, new FakeUser("mara"));
    await world.ensureTable();

    const created = await world.create(row({ name: "tracked" }));
    expect(created["created_at"]).toEqual(clock.utcNow);
    expect(created["created_by"]).toBe("mara");

    const read = await world.find(created.id);
    expect(read!["created_at"]).toEqual(clock.utcNow);
    expect(read!["created_by"]).toBe("mara");
    expect(read!["updated_at"]).toBeNull();
    expect(read!["updated_by"]).toBeNull();

    clock.utcNow = Instant.parse("2026-01-05T03:04:05.654321Z")!;
    const updated = await world.update(created.id, row({ name: "renamed" }), ["name"]);

    expect(updated["created_at"]).toEqual(Instant.parse("2026-01-02T03:04:05.123456Z"));
    expect(updated["created_by"]).toBe("mara");
    expect(updated["updated_at"]).toEqual(clock.utcNow);
    expect(updated["updated_by"]).toBe("mara");
  });

  it("a partial update touches only the named fields", async () => {
    const world = store();
    await world.ensureTable();

    const created = await world.create(
      row({ name: "first", count: 10, tags: ["keep", "me"] }),
    );

    const updated = await world.update(created.id, row({ name: "second", count: 99 }), ["name"]);

    expect(updated["name"]).toBe("second");
    expect(updated["count"]).toBe(10);
    expect(updated["tags"]).toEqual(["keep", "me"]);
  });

  it("a duplicate id is a 409, not a driver error", async () => {
    const world = store();
    await world.ensureTable();

    await world.create(row({ id: "dup", name: "one" }));

    await expect(world.create(row({ id: "dup", name: "two" }))).rejects.toMatchObject({
      code: "record.duplicate_id",
      statusCode: 409,
    });
  });

  it("update and delete of a missing row are 404s", async () => {
    const world = store();
    await world.ensureTable();

    await expect(world.update("nope", row({ name: "x" }), ["name"])).rejects.toMatchObject({
      code: "record.not_found",
      statusCode: 404,
    });
    await expect(world.delete("nope")).rejects.toMatchObject({
      code: "record.not_found",
      statusCode: 404,
    });
  });

  it("hooks fire in order around the SQL calls", async () => {
    const log: string[] = [];
    const hooks = new RecordHooks<Everything>(
      [(record) => log.push(`before-create:${record.name}`)],
      [() => log.push("after-create")],
      [() => log.push("before-update")],
      [() => log.push("after-update")],
      [() => log.push("before-delete")],
      [() => log.push("after-delete")],
    );

    const world = new SqlRecordStore(
      descriptor,
      hooks,
      new FakeUser("someone"),
      new FixedClock(Instant.parse("2026-01-02T03:04:05Z")!),
      driver,
    );
    await world.ensureTable();

    const created = await world.create(row({ name: "hooked" }));
    await world.update(created.id, row({ name: "re-hooked" }), ["name"]);
    await world.delete(created.id);

    expect(log).toEqual([
      "before-create:hooked",
      "after-create",
      "before-update",
      "after-update",
      "before-delete",
      "after-delete",
    ]);
  });

  it("query returns every row", async () => {
    const world = store();
    await world.ensureTable();

    await world.create(row({ id: "q1", name: "one" }));
    await world.create(row({ id: "q2", name: "two" }));

    const rows = await world.query();
    const ids = rows.map((r) => r.id);
    expect(ids).toContain("q1");
    expect(ids).toContain("q2");
  });

  it("a store without tracking skips the tracking columns", async () => {
    const plain = new RecordDescriptor<Everything>("plain", "Plain", [
      { key: "name", type: "text" },
    ]);
    const world = new SqlRecordStore(
      plain,
      noHooks,
      new FakeUser("someone"),
      new FixedClock(Instant.parse("2026-01-02T03:04:05Z")!),
      driver,
      undefined,
      false,
    );
    await world.ensureTable();

    const created = await world.create(row({ name: "bare" }));
    const read = await world.find(created.id);

    expect(read!["name"]).toBe("bare");
    expect(read!["created_at"]).toBeUndefined();
  });
});
