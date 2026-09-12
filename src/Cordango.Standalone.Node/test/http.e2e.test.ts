// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { Server } from "node:http";
import type { AddressInfo } from "node:net";
import { Router } from "express";
import {
  CommandCatalogue,
  CordangoRuntime,
  PgliteDriver,
  RecordDescriptor,
  addDirectory,
  createApp,
  recordsRouter,
  type AppPermissions,
  type RecordRow,
} from "../src/index.js";

const expense = new RecordDescriptor<RecordRow>("expense", "Expense", [
  { key: "description", type: "text" },
  { key: "amount", type: "money" },
  { key: "category", type: "text" },
  { key: "status", type: "text" },
  { key: "secret_note", type: "text" },
]);

const permissions: AppPermissions = {
  roles: [
    {
      key: "employee",
      grants: [
        {
          entity: "expense",
          create: true,
          read: true,
          update: true,
          commands: ["submit"],
          fieldOverrides: [{ field: "secret_note", read: false, update: false }],
        },
      ],
    },
  ],
};

const commands = new CommandCatalogue([
  {
    key: "submit",
    label: "Submit",
    entity: "expense",
    stateField: "status",
    fromStates: ["draft"],
    toState: "submitted",
    successMessage: "Sent for approval.",
  },
]);

class Client {
  private cookies = new Map<string, string>();

  constructor(private readonly origin: string) {}

  get xsrf(): string | undefined {
    return this.cookies.get("XSRF-TOKEN");
  }

  async send(
    method: string,
    path: string,
    body?: unknown,
  ): Promise<{ status: number; body: unknown }> {
    const headers: Record<string, string> = { Accept: "application/json" };

    const cookie = [...this.cookies].map(([name, value]) => `${name}=${value}`).join("; ");
    if (cookie !== "") headers["Cookie"] = cookie;

    if (!["GET", "HEAD", "OPTIONS"].includes(method) && this.xsrf !== undefined)
      headers["X-XSRF-TOKEN"] = this.xsrf;

    if (body !== undefined) headers["Content-Type"] = "application/json";

    const response = await fetch(`${this.origin}${path}`, {
      method,
      headers,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });

    for (const header of response.headers.getSetCookie()) {
      const pair = header.split(";")[0] ?? "";
      const separator = pair.indexOf("=");
      if (separator <= 0) continue;
      const name = pair.slice(0, separator).trim();
      const value = decodeURIComponent(pair.slice(separator + 1).trim());
      if (value === "") this.cookies.delete(name);
      else this.cookies.set(name, value);
    }

    const text = await response.text();
    return { status: response.status, body: text === "" ? null : JSON.parse(text) };
  }

  /** A request that deliberately omits the antiforgery header. */
  async sendWithoutToken(method: string, path: string, body: unknown): Promise<number> {
    const cookie = [...this.cookies].map(([name, value]) => `${name}=${value}`).join("; ");
    const response = await fetch(`${this.origin}${path}`, {
      method,
      headers: { "Content-Type": "application/json", Cookie: cookie },
      body: JSON.stringify(body),
    });
    return response.status;
  }
}

describe("the HTTP surface a generated application serves", () => {
  let driver: PgliteDriver;
  let server: Server;
  let origin: string;

  beforeAll(async () => {
    driver = new PgliteDriver();

    const runtime = new CordangoRuntime({
      appKey: "expenses",
      appName: "Expenses",
      driver,
      permissions,
      commands,
      secret: "a-test-secret-nobody-would-ship",
    });

    addDirectory(runtime);
    runtime.register({ descriptor: expense });
    await runtime.start();

    const app = createApp({
      runtime,
      routes: (api: Router) => {
        api.use("/expense", recordsRouter("expense"));
      },
    });

    server = app.listen(0);
    await new Promise<void>((resolve) => server.once("listening", resolve));
    origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  }, 30_000);

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()));
    await driver.close();
  });

  it("reports that a fresh database needs setting up", async () => {
    const client = new Client(origin);
    const answer = await client.send("GET", "/api/account/me");

    expect(answer.status).toBe(200);
    expect(answer.body).toEqual({ authenticated: false, setupRequired: true });
  });

  it("issues an antiforgery token on a safe request", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");
    expect(client.xsrf).toBeTypeOf("string");
  });

  it("refuses an unsafe request that does not echo the token", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");

    const status = await client.sendWithoutToken("POST", "/api/account/login", {
      email: "nobody@example.com",
      password: "irrelevant",
    });

    expect(status).toBe(400);
  });

  it("refuses a password shorter than the minimum, and creates nobody", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");

    const answer = await client.send("POST", "/api/account/setup", {
      email: "admin@example.com",
      password: "short",
      displayName: "Admin",
    });

    expect(answer.status).toBe(400);
    expect((answer.body as { code: string }).code).toBe("auth.password_rejected");

    const me = await client.send("GET", "/api/account/me");
    expect((me.body as { setupRequired: boolean }).setupRequired).toBe(true);
  });

  it("creates the first administrator and signs them in", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");

    const answer = await client.send("POST", "/api/account/setup", {
      email: "admin@example.com",
      password: "a-long-enough-password",
      displayName: "The Administrator",
    });

    expect(answer.status).toBe(200);
    expect(answer.body).toMatchObject({
      authenticated: true,
      email: "admin@example.com",
      isAdministrator: true,
      roles: ["Administrator"],
    });

    const me = await client.send("GET", "/api/account/me");
    expect(me.body).toMatchObject({ authenticated: true, isAdministrator: true });
  });

  it("refuses a second setup once an administrator exists", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");

    const answer = await client.send("POST", "/api/account/setup", {
      email: "second@example.com",
      password: "a-long-enough-password",
    });

    expect(answer.status).toBe(409);
    expect((answer.body as { code: string }).code).toBe("setup.completed");
  });

  it("answers the same way for a wrong password and an unknown address", async () => {
    const client = new Client(origin);
    await client.send("GET", "/api/account/me");

    const wrongPassword = await client.send("POST", "/api/account/login", {
      email: "admin@example.com",
      password: "not-the-password",
    });
    const noSuchAccount = await client.send("POST", "/api/account/login", {
      email: "ghost@example.com",
      password: "not-the-password",
    });

    expect(wrongPassword.status).toBe(401);
    expect(noSuchAccount.status).toBe(401);
    expect(wrongPassword.body).toEqual(noSuchAccount.body);
  });

  it("refuses record routes to an anonymous caller with 401, not 403", async () => {
    const client = new Client(origin);
    const answer = await client.send("GET", "/api/expense");

    expect(answer.status).toBe(401);
    expect((answer.body as { code: string }).code).toBe("auth.required");
  });

  it("answers an unknown API route as JSON rather than as the single-page app", async () => {
    const client = new Client(origin);
    const answer = await client.send("GET", "/api/nothing-here");

    expect(answer.status).toBe(404);
    expect((answer.body as { code: string }).code).toBe("route.not_found");
  });

  describe("with an administrator signed in", () => {
    let admin: Client;

    beforeAll(async () => {
      admin = new Client(origin);
      await admin.send("GET", "/api/account/me");
      const answer = await admin.send("POST", "/api/account/login", {
        email: "admin@example.com",
        password: "a-long-enough-password",
      });
      expect(answer.status).toBe(200);
    });

    it("creates, reads, updates and deletes a record", async () => {
      const created = await admin.send("POST", "/api/expense", {
        description: "Taxi",
        amount: 42.5,
        category: "travel",
        status: "draft",
      });

      expect(created.status).toBe(201);
      const id = (created.body as { id: string }).id;
      expect(id).toBeTypeOf("string");
      expect(created.body).toMatchObject({ description: "Taxi", amount: 42.5 });

      const read = await admin.send("GET", `/api/expense/${id}`);
      expect(read.status).toBe(200);
      expect(read.body).toMatchObject({ description: "Taxi" });

      const patched = await admin.send("PATCH", `/api/expense/${id}`, { category: "transport" });
      expect(patched.status).toBe(200);
      expect(patched.body).toMatchObject({ description: "Taxi", category: "transport" });

      const deleted = await admin.send("DELETE", `/api/expense/${id}`);
      expect(deleted.status).toBe(204);

      const gone = await admin.send("GET", `/api/expense/${id}`);
      expect(gone.status).toBe(404);
    });

    it("clears a field a replace leaves out, and keeps one a patch leaves out", async () => {
      const created = await admin.send("POST", "/api/expense", {
        description: "Hotel",
        amount: 120,
        category: "travel",
        status: "draft",
      });
      const id = (created.body as { id: string }).id;

      const patched = await admin.send("PATCH", `/api/expense/${id}`, { amount: 130 });
      expect(patched.body).toMatchObject({ category: "travel", amount: 130 });

      const replaced = await admin.send("PUT", `/api/expense/${id}`, {
        description: "Hotel",
        amount: 130,
        status: "draft",
      });
      expect((replaced.body as { category: unknown }).category).toBeNull();
    });

    it("pages and counts a list independently", async () => {
      const answer = await admin.send("GET", "/api/expense?take=1&skip=0");
      const page = answer.body as { items: unknown[]; total: number; take: number };

      expect(page.items.length).toBeLessThanOrEqual(1);
      expect(page.total).toBeGreaterThanOrEqual(page.items.length);
      expect(page.take).toBe(1);
    });

    it("aggregates without the caller downloading the rows", async () => {
      const answer = await admin.send("GET", "/api/expense/aggregate?op=count");
      expect(answer.status).toBe(200);
      expect(answer.body).toMatchObject({ op: "count" });
    });

    it("runs a command and refuses it from the wrong state", async () => {
      const created = await admin.send("POST", "/api/expense", {
        description: "Train",
        amount: 30,
        status: "draft",
      });
      const id = (created.body as { id: string }).id;

      const first = await admin.send("POST", `/api/expense/${id}/commands/submit`, {});
      expect(first.status).toBe(200);
      expect(first.body).toMatchObject({ message: "Sent for approval." });
      expect((first.body as { record: { status: string } }).record.status).toBe("submitted");

      const second = await admin.send("POST", `/api/expense/${id}/commands/submit`, {});
      expect(second.status).toBe(409);
      expect((second.body as { code: string }).code).toBe("command.illegal_transition");
    });

    it("gives the administrator a directory Person, so their own screens are not empty", async () => {
      const me = await admin.send("GET", "/api/account/me");
      const personId = (me.body as { personId: string | null }).personId;

      // Without this a login exists and no person does, so every screen the definition filters by
      // person — "expenses I submitted" — renders empty with nothing to say why.
      expect(personId).toBeTypeOf("string");

      const person = await admin.send("GET", `/api/directory/person/${personId ?? ""}`);
      expect(person.status).toBe(200);
      expect(person.body).toMatchObject({ email: "admin@example.com", has_login: true });
    });

    it("reads the directory and lists the declared roles", async () => {
      const people = await admin.send("GET", "/api/directory/person?take=500");
      expect(people.status).toBe(200);

      const roles = await admin.send("GET", "/api/admin/roles");
      expect(roles.body).toMatchObject({ administrator: "Administrator" });
      expect((roles.body as { roles: { key: string }[] }).roles.map((r) => r.key)).toEqual(["employee"]);
    });

    it("will not let the only administrator demote, lock or delete themselves", async () => {
      const me = await admin.send("GET", "/api/account/me");
      const id = (me.body as { id: string }).id;

      const demoted = await admin.send("PUT", `/api/admin/users/${id}`, { roles: ["employee"] });
      expect(demoted.status).toBe(400);
      expect((demoted.body as { code: string }).code).toBe("user.self_demote");

      const locked = await admin.send("POST", `/api/admin/users/${id}/lock`, { locked: true });
      expect((locked.body as { code: string }).code).toBe("user.self_lock");

      const deleted = await admin.send("DELETE", `/api/admin/users/${id}`);
      expect((deleted.body as { code: string }).code).toBe("user.self_delete");
    });

    it("refuses a role no definition declares", async () => {
      const answer = await admin.send("POST", "/api/admin/users", {
        email: "someone@example.com",
        password: "a-long-enough-password",
        roles: ["wizard"],
      });

      expect(answer.status).toBe(400);
      expect((answer.body as { code: string }).code).toBe("user.role_unknown");
    });

    it("saves and reads back one person's table layout", async () => {
      await admin.send("PUT", "/api/settings/table/expenses/main", { columns: ["description"] });
      const answer = await admin.send("GET", "/api/settings/table/expenses/main");
      expect(answer.body).toEqual({ columns: ["description"] });
    });
  });

  describe("with a narrow role signed in", () => {
    let employee: Client;
    let employeeId: string;

    beforeAll(async () => {
      const admin = new Client(origin);
      await admin.send("GET", "/api/account/me");
      await admin.send("POST", "/api/account/login", {
        email: "admin@example.com",
        password: "a-long-enough-password",
      });

      const created = await admin.send("POST", "/api/admin/users", {
        email: "employee@example.com",
        password: "another-long-password",
        displayName: "An Employee",
        roles: ["employee"],
      });
      expect(created.status).toBe(201);
      employeeId = (created.body as { id: string }).id;

      employee = new Client(origin);
      await employee.send("GET", "/api/account/me");
      const signedIn = await employee.send("POST", "/api/account/login", {
        email: "employee@example.com",
        password: "another-long-password",
      });
      expect(signedIn.status).toBe(200);
    });

    it("is told to change the password an administrator chose", async () => {
      const me = await employee.send("GET", "/api/account/me");
      expect(me.body).toMatchObject({ mustChangePassword: true, isAdministrator: false });
    });

    it("never sees a field its role may not read", async () => {
      const created = await employee.send("POST", "/api/expense", {
        description: "Coffee",
        amount: 4,
        status: "draft",
      });

      expect(created.status).toBe(201);
      expect(created.body).not.toHaveProperty("secret_note");

      const listed = await employee.send("GET", "/api/expense");
      for (const item of (listed.body as { items: Record<string, unknown>[] }).items)
        expect(item).not.toHaveProperty("secret_note");
    });

    it("is refused a write to a field its role may not set, by name", async () => {
      const created = await employee.send("POST", "/api/expense", {
        description: "Lunch",
        amount: 9,
        status: "draft",
        secret_note: "should not be allowed",
      });

      expect(created.status).toBe(403);
      expect(created.body).toMatchObject({
        code: "record.field_write_denied",
        fields: ["secret_note"],
      });
    });

    it("cannot delete, because no grant of its role says so", async () => {
      const listed = await employee.send("GET", "/api/expense?take=1");
      const first = (listed.body as { items: { id: string }[] }).items[0];
      expect(first).toBeDefined();

      const answer = await employee.send("DELETE", `/api/expense/${first?.id ?? ""}`);
      expect(answer.status).toBe(403);
      expect((answer.body as { code: string }).code).toBe("record.delete_denied");
    });

    it("cannot sum a field it cannot read", async () => {
      const answer = await employee.send("GET", "/api/expense/aggregate?op=sum&field=secret_note");
      expect(answer.status).toBe(403);
    });

    it("cannot reach the administration routes at all", async () => {
      const answer = await employee.send("GET", "/api/admin/users");
      expect(answer.status).toBe(403);
      expect((answer.body as { code: string }).code).toBe("auth.forbidden");
    });

    it("may write the directory no more than it may read it", async () => {
      const read = await employee.send("GET", "/api/directory/person");
      expect(read.status).toBe(200);

      const write = await employee.send("POST", "/api/directory/person", { full_name: "Nobody" });
      expect(write.status).toBe(403);
    });

    it("was given a Person of its own when the administrator created it", async () => {
      const me = await employee.send("GET", "/api/account/me");
      expect((me.body as { personId: string | null }).personId).toBeTypeOf("string");
    });

    it("loses every session the moment an administrator resets its password", async () => {
      const before = await employee.send("GET", "/api/account/me");
      expect((before.body as { authenticated: boolean }).authenticated).toBe(true);

      const admin = new Client(origin);
      await admin.send("GET", "/api/account/me");
      await admin.send("POST", "/api/account/login", {
        email: "admin@example.com",
        password: "a-long-enough-password",
      });
      const reset = await admin.send("POST", `/api/admin/users/${employeeId}/password`, {
        password: "a-replacement-password",
      });
      expect(reset.status).toBe(204);

      const after = await employee.send("GET", "/api/account/me");
      expect((after.body as { authenticated: boolean }).authenticated).toBe(false);
    });
  });
});
