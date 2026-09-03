// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import {
  EntityAccess,
  RecordDescriptor,
  RecordGateway,
  RecordHooks,
  RecordStore,
  type AppPermissions,
  type CurrentUser,
  type RecordRow,
} from "../src/index.js";

type Widget = RecordRow & { name: string | null; amount: number; secret: string | null };

const descriptor = new RecordDescriptor<Widget>("widget", "widget", [
  { key: "name", type: "text" },
  { key: "amount", type: "integer" },
  { key: "secret", type: "text" },
]);

const fullAccess = EntityAccess.full;
const noSecret = new EntityAccess(true, true, true, true, new Map([["secret", { read: false, update: false }]]));
const noSecretWrite = new EntityAccess(true, true, true, true, new Map([["secret", { read: true, update: false }]]));

class World {
  readonly store: RecordStore<Widget>;
  readonly gateway: RecordGateway<Widget>;

  constructor(
    access: EntityAccess,
    permissions: AppPermissions = { roles: [] },
    caller: CurrentUser = { userId: "someone", isAdministrator: false, roleKeys: [] },
  ) {
    this.store = new RecordStore(
      descriptor,
      RecordHooks.none as RecordHooks<Widget>,
      caller,
      { utcNow: { epochMicros: 0n } as never },
    );

    this.gateway = new (class extends RecordGateway<Widget> {
      protected override resolveAccess(): EntityAccess {
        return access;
      }
    })(this.store, permissions, caller, {
      run: async () => ({ record: null, message: null }),
    });
  }
}

describe("RecordGateway", () => {
  it("lists with paging, counting before the page is taken", async () => {
    const world = new World(fullAccess);
    for (let i = 1; i <= 5; i++)
      await world.store.create({ id: String(i), name: `w${i}`, amount: i, secret: null });

    const page = await world.gateway.list([], [], 2, 2);

    expect(page.total).toBe(5);
    expect(page.skip).toBe(2);
    expect(page.take).toBe(2);
    expect(page.items.map((row) => row.id)).toEqual(["3", "4"]);
  });

  it("clamps take to the ceiling and skip to zero", async () => {
    const world = new World(fullAccess);
    await world.store.create({ id: "1", name: "w", amount: 1, secret: null });

    const greedy = await world.gateway.list([], [], -5, 9999);
    expect(greedy.take).toBe(500);
    expect(greedy.skip).toBe(0);
  });

  it("removes fields the role may not read from every row", async () => {
    const world = new World(noSecret);
    await world.store.create({ id: "1", name: "w", amount: 1, secret: "shh" });

    const page = await world.gateway.list([], [], 0, 10);
    expect(page.items[0]).not.toHaveProperty("secret");
    expect(page.items[0]).toHaveProperty("name", "w");

    const one = await world.gateway.get("1");
    expect(one).not.toHaveProperty("secret");
  });

  it("refuses a write that touches a restricted field, naming every offender at once", async () => {
    const world = new World(noSecretWrite);
    await world.store.create({ id: "1", name: "w", amount: 1, secret: null });

    await expect(
      world.gateway.create({ id: "2", name: "x", amount: 2, secret: "shh" }),
    ).rejects.toMatchObject({
      code: "record.field_write_denied",
      fields: ["secret"],
      statusCode: 403,
    });
  });

  it("answers 403 rather than 404 when the role may not", async () => {
    const world = new World(EntityAccess.none);

    await expect(world.gateway.list([], [], 0, 10)).rejects.toMatchObject({
      code: "record.read_denied",
      statusCode: 403,
    });
    await expect(world.gateway.get("1")).rejects.toMatchObject({ code: "record.read_denied", statusCode: 403 });
    await expect(world.gateway.create({ name: "x" })).rejects.toMatchObject({
      code: "record.create_denied",
      statusCode: 403,
    });
    await expect(world.gateway.write("1", { name: "x" }, ["name"])).rejects.toMatchObject({
      code: "record.update_denied",
      statusCode: 403,
    });
    await expect(world.gateway.delete("1")).rejects.toMatchObject({
      code: "record.delete_denied",
      statusCode: 403,
    });
  });

  it("refuses an aggregate over a field the role may not read", async () => {
    const world = new World(noSecret);
    await world.store.create({ id: "1", name: "w", amount: 1, secret: "shh" });

    await expect(world.gateway.aggregateRows("sum", "secret", null, [])).rejects.toMatchObject({
      code: "record.read_denied",
      statusCode: 403,
    });
    await expect(world.gateway.aggregateRows("count", null, "secret", [])).rejects.toMatchObject({
      code: "record.read_denied",
      statusCode: 403,
    });
  });

  it("refuses a body that is not a JSON object", async () => {
    const world = new World(fullAccess);

    await expect(world.gateway.create(["not", "an", "object"])).rejects.toMatchObject({
      code: "request.body_invalid",
    });
  });

  it("refuses a value that does not parse against the field's type", async () => {
    const world = new World(fullAccess);

    await expect(world.gateway.create({ name: "x", amount: "ten" })).rejects.toMatchObject({
      code: "request.body_invalid",
    });
  });

  it("distinguishes absent from explicitly null through the supplied keys", async () => {
    const world = new World(fullAccess);

    expect(world.gateway.suppliedKeys({ name: "x", amount: null })).toEqual(["name", "amount"]);
    expect(world.gateway.suppliedKeys("not an object")).toEqual([]);
  });

  it("writes only the named fields, and a replace writes them all", async () => {
    const world = new World(fullAccess);
    await world.store.create({ id: "1", name: "first", amount: 10, secret: null });

    const patched = await world.gateway.write("1", { amount: 42 }, ["amount"]);
    expect(patched["name"]).toBe("first");

    const replaced = await world.gateway.write("1", { name: "second" }, world.gateway.fieldKeys);
    expect(replaced["name"]).toBe("second");
    expect(replaced["amount"]).toBe(0);
  });
});
