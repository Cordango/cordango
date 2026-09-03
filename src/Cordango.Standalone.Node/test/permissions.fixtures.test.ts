// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import { resolveRoles, type AppPermissions, type EntityGrant, type FieldOverride } from "../src/index.js";
import { fixtureFiles, loadFixture } from "./fixtures.js";

type Case = {
  roleKeys: string[];
  entity: string;
  expect: { create: boolean; read: boolean; update: boolean; delete: boolean };
  fields?: Record<string, { read: boolean; update: boolean }>;
  commands?: Record<string, boolean>;
};

describe("permission fixtures", () => {
  expect(fixtureFiles("permissions").length).toBeGreaterThanOrEqual(6);

  for (const file of fixtureFiles("permissions")) {
    const fixture = loadFixture("permissions", file);
    const permissions = readRoles(fixture["roles"]);
    const cases = fixture["cases"] as Case[];

    describe(file, () => {
      expect(cases.length).toBeGreaterThan(0);

      for (const [index, scenario] of cases.entries()) {
        it(`case ${index}: [${scenario.roleKeys.join(", ")}] on ${scenario.entity}`, () => {
          const access = resolveRoles(permissions, scenario.roleKeys, scenario.entity);

          expect(access.create).toBe(scenario.expect.create);
          expect(access.read).toBe(scenario.expect.read);
          expect(access.update).toBe(scenario.expect.update);
          expect(access.delete_).toBe(scenario.expect.delete);

          for (const [field, rule] of Object.entries(scenario.fields ?? {})) {
            expect(access.canReadField(field), `field '${field}' read`).toBe(rule.read);
            expect(access.canUpdateField(field), `field '${field}' update`).toBe(rule.update);
          }

          for (const [command, allowed] of Object.entries(scenario.commands ?? {})) {
            expect(access.canRunCommand(command), `command '${command}'`).toBe(allowed);
          }
        });
      }
    });
  }
});

function readRoles(json: unknown): AppPermissions {
  if (!Array.isArray(json)) return { roles: [] };

  const roles = [];
  for (const role of json as Record<string, unknown>[]) {
    if (typeof role["key"] !== "string") continue;

    const grants: EntityGrant[] = [];
    for (const grant of (role["grants"] as Record<string, unknown>[] | undefined) ?? []) {
      if (typeof grant["entity"] !== "string") continue;

      const overrides: FieldOverride[] = [];
      for (const override of (grant["fieldOverrides"] as Record<string, unknown>[] | undefined) ?? []) {
        if (typeof override["field"] !== "string") continue;
        const rule: FieldOverride = { field: override["field"] };
        if (typeof override["read"] === "boolean") rule.read = override["read"];
        if (typeof override["update"] === "boolean") rule.update = override["update"];
        overrides.push(rule);
      }

      grants.push({
        entity: grant["entity"],
        create: grant["create"] === true,
        read: grant["read"] === true,
        update: grant["update"] === true,
        delete: grant["delete"] === true,
        fieldOverrides: overrides,
        commands: ((grant["commands"] as unknown[] | undefined) ?? []).filter(
          (c): c is string => typeof c === "string",
        ),
      });
    }

    roles.push({ key: role["key"], grants });
  }

  return { roles };
}
