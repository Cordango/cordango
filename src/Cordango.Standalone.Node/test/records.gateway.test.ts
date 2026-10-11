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

type Part = RecordRow & { barcode: string | null };
type Line = RecordRow & { item: string | null; quantity: number };

const partDescriptor = new RecordDescriptor<Part>("part", "part", [{ key: "barcode", type: "text" }]);
const lineDescriptor = new RecordDescriptor<Line>("line", "line", [
  { key: "item", type: "reference", target: "part" },
  { key: "quantity", type: "integer" },
]);

const noBarcode = new EntityAccess(true, true, true, true, new Map([["barcode", { read: false, update: false }]]));
const noItem = new EntityAccess(true, true, true, true, new Map([["item", { read: false, update: false }]]));

async function hopWorld(partAccess: EntityAccess, lineAccess: EntityAccess = fullAccess) {
  const caller: CurrentUser = { userId: "someone", isAdministrator: false, roleKeys: [] };
  const clock = { utcNow: { epochMicros: 0n } as never };
  const parts = new RecordStore(partDescriptor, RecordHooks.none as RecordHooks<Part>, caller, clock);
  const lines = new RecordStore(lineDescriptor, RecordHooks.none as RecordHooks<Line>, caller, clock);
  const commands = { run: async () => ({ record: null, message: null }) };

  const partGateway = new (class extends RecordGateway<Part> {
    protected override resolveAccess(): EntityAccess {
      return partAccess;
    }
  })(parts, { roles: [] }, caller, commands);

  const lineGateway = new (class extends RecordGateway<Line> {
    protected override resolveAccess(): EntityAccess {
      return lineAccess;
    }
  })(lines, { roles: [] }, caller, commands, (entity) =>
    entity === "part" ? (partGateway as unknown as RecordGateway<RecordRow>) : undefined);

  await parts.create({ id: "p1", barcode: "111" });
  await parts.create({ id: "p2", barcode: "222" });
  await lines.create({ id: "l1", item: "p1", quantity: 1 });
  await lines.create({ id: "l2", item: "p2", quantity: 2 });
  await lines.create({ id: "l3", item: null, quantity: 3 });

  return lineGateway;
}

describe("a filter that follows a reference", () => {
  it("narrows to the rows whose reference matched", async () => {
    const lines = await hopWorld(fullAccess);

    const page = await lines.list([{ field: "item.barcode", operator: "eq", value: "222" }], [], 0, 10);

    expect(page.items.map((row) => row.id)).toEqual(["l2"]);
  });

  it("combines with the entity's own filters", async () => {
    const lines = await hopWorld(fullAccess);

    const page = await lines.list(
      [
        { field: "item.barcode", operator: "in", value: "111|222" },
        { field: "quantity", operator: "gt", value: "1" },
      ],
      [],
      0,
      10,
    );

    expect(page.items.map((row) => row.id)).toEqual(["l2"]);
  });

  it("is refused when the role may not read the field on the other side", async () => {
    const lines = await hopWorld(noBarcode);

    await expect(lines.list([{ field: "item.barcode", operator: "eq", value: "111" }], [], 0, 10))
      .rejects.toMatchObject({ code: "record.read_denied" });
  });

  it("is refused when the role may not read the reference itself", async () => {
    const lines = await hopWorld(fullAccess, noItem);

    await expect(lines.list([{ field: "item.barcode", operator: "eq", value: "111" }], [], 0, 10))
      .rejects.toMatchObject({ code: "record.read_denied" });
  });

  it("is refused when the field is not a reference it can follow", async () => {
    const lines = await hopWorld(fullAccess);

    await expect(lines.list([{ field: "quantity.barcode", operator: "eq", value: "111" }], [], 0, 10))
      .rejects.toMatchObject({ code: "query.path_invalid" });
  });
});
