// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import {
  Instant,
  RecordDescriptor,
  RecordError,
  RecordHooks,
  RecordStore,
  type Clock,
  type CurrentUser,
  type RecordRow,
} from "../src/index.js";

type Widget = RecordRow & {
  name: string | null;
  amount: number;
  note: string | null;
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

class World {
  readonly clock: FixedClock;
  readonly user: FakeUser;
  readonly log: string[] = [];
  refuse = false;
  onBeforeUpdate: ((after: Widget, before: Widget) => void) | null = null;

  readonly store: RecordStore<Widget>;

  constructor(clock?: FixedClock, user?: FakeUser) {
    this.clock = clock ?? new FixedClock(Instant.parse("1970-01-01T00:00:00Z")!);
    this.user = user ?? new FakeUser("someone");

    const descriptor = new RecordDescriptor<Widget>("widget", "widget", [
      { key: "name", type: "text" },
      { key: "amount", type: "integer" },
      { key: "note", type: "text" },
    ]);

    const recorder = {
      beforeCreate: (record: Widget) => {
        this.log.push("before-create:2");
        if (this.refuse) throw new RecordError("widget.refused", "Not this one.");
      },
      afterCreate: () => this.log.push("after-create"),
      beforeUpdate: (after: Widget, before: Widget) => {
        this.log.push("before-update");
        this.onBeforeUpdate?.(after, before);
      },
      afterUpdate: () => this.log.push("after-update"),
      beforeDelete: () => this.log.push("before-delete"),
      afterDelete: () => this.log.push("after-delete"),
    };

    const hooks = new RecordHooks<Widget>(
      [
        () => this.log.push("before-create:1"),
        (record: Widget) => recorder.beforeCreate(record),
      ],
      [() => recorder.afterCreate()],
      [(after: Widget, before: Widget) => recorder.beforeUpdate(after, before)],
      [() => recorder.afterUpdate()],
      [() => recorder.beforeDelete()],
      [() => recorder.afterDelete()],
    );

    this.store = new RecordStore(descriptor, hooks, this.user, this.clock);
  }
}

function widget(init: Partial<Widget> = {}): Widget {
  return { id: "", name: null, amount: 0, note: null, ...init };
}

describe("RecordStore", () => {
  it("create stamps who and when, and update leaves the creation alone", async () => {
    const clock = new FixedClock(Instant.parse("2026-01-02T03:04:05Z")!);
    const world = new World(clock, new FakeUser("mara"));

    const created = await world.store.create(widget({ name: "First", amount: 10 }));

    expect(created["created_at"]).toEqual(clock.utcNow);
    expect(created["created_by"]).toBe("mara");
    expect(created["updated_at"]).toBeUndefined();

    clock.utcNow = Instant.parse("2026-01-05T03:04:05Z")!;
    world.user.userId = "tim";

    const updated = await world.store.update(created.id, widget({ amount: 25 }), ["amount"]);

    expect(updated["created_at"]).toEqual(Instant.parse("2026-01-02T03:04:05Z"));
    expect(updated["created_by"]).toBe("mara");
    expect(updated["updated_at"]).toEqual(clock.utcNow);
    expect(updated["updated_by"]).toBe("tim");
  });

  it("an already stamped row keeps its own creation time", async () => {
    const clock = new FixedClock(Instant.parse("2026-08-01T00:00:00Z")!);
    const world = new World(clock, new FakeUser("mara"));

    const authored = Instant.parse("2026-03-14T09:00:00Z")!;
    const created = await world.store.create(
      widget({ name: "Imported", created_at: authored, created_by: "importer" }),
    );

    expect(created["created_at"]).toEqual(authored);
    expect(created["created_by"]).toBe("importer");
  });

  it("a partial update touches only the named fields", async () => {
    const world = new World();
    const created = await world.store.create(widget({ name: "First", amount: 10, note: "keep me" }));

    const updated = await world.store.update(created.id, widget({ amount: 42 }), ["amount"]);

    expect(updated.amount).toBe(42);
    expect(updated.name).toBe("First");
    expect(updated.note).toBe("keep me");
  });

  it("a replace writes every field", async () => {
    const world = new World();
    const created = await world.store.create(widget({ name: "First", amount: 10, note: "keep me" }));

    const updated = await world.store.update(created.id, widget({ name: "Second" }), world.store.descriptor.fieldKeys);

    expect(updated.name).toBe("Second");
    expect(updated.amount).toBe(0);
    expect(updated.note).toBeNull();
  });

  it("hooks run in order and are awaited", async () => {
    const world = new World();

    const created = await world.store.create(widget({ name: "First" }));
    await world.store.update(created.id, widget({ name: "Second" }), ["name"]);
    await world.store.delete(created.id);

    expect(world.log).toEqual([
      "before-create:1",
      "before-create:2",
      "after-create",
      "before-update",
      "after-update",
      "before-delete",
      "after-delete",
    ]);
  });

  it("a refusing hook stops the write and the row is not there", async () => {
    const world = new World();
    world.refuse = true;

    const refused = world.store.create(widget({ id: "w1", name: "First" }));

    await expect(refused).rejects.toMatchObject({ code: "widget.refused" });
    await expect(world.store.find("w1")).resolves.toBeUndefined();
  });

  it("an update hook sees both versions", async () => {
    const world = new World();
    const created = await world.store.create(widget({ name: "First", amount: 1 }));

    let seen: { after: Widget; before: Widget } | null = null;
    world.onBeforeUpdate = (after, before) => (seen = { after, before });

    await world.store.update(created.id, widget({ amount: 99 }), ["amount"]);

    expect(seen).not.toBeNull();
    expect(seen!.before.amount).toBe(1);
    expect(seen!.after.amount).toBe(99);
  });

  it("a client chosen id is kept and a taken one is refused", async () => {
    const world = new World();

    const chosen = await world.store.create(widget({ id: "eur", name: "Euro" }));
    expect(chosen.id).toBe("eur");

    const clash = world.store.create(widget({ id: "eur", name: "Also Euro" }));
    await expect(clash).rejects.toMatchObject({ code: "record.duplicate_id", statusCode: 409 });
  });

  it("an update cannot change a record's identity", async () => {
    const world = new World();
    const created = await world.store.create(widget({ id: "w1", name: "First" }));

    const updated = await world.store.update(created.id, widget({ id: "somewhere-else", name: "Second" }), ["name"]);

    expect(updated.id).toBe("w1");
    await expect(world.store.find("w1")).resolves.toBeDefined();
  });

  it("updating or deleting something that is not there says so", async () => {
    const world = new World();

    await expect(world.store.update("nope", widget(), ["name"])).rejects.toMatchObject({
      code: "record.not_found",
      statusCode: 404,
    });
    await expect(world.store.delete("nope")).rejects.toMatchObject({ code: "record.not_found", statusCode: 404 });
  });
});
