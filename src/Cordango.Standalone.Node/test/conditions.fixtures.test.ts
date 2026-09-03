// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import { Instant, evaluateCondition, readCondition } from "../src/index.js";
import { fixtureFiles, loadFixture } from "./fixtures.js";

type Case = { record: Record<string, unknown>; condition: unknown; expect: boolean; why?: string };

describe("condition fixtures", () => {
  expect(fixtureFiles("conditions").length).toBeGreaterThanOrEqual(5);

  for (const file of fixtureFiles("conditions")) {
    const fixture = loadFixture("conditions", file);
    const actorId = (fixture["actorId"] as string | undefined) ?? null;
    const now = Instant.parse((fixture["now"] as string | undefined) ?? "2026-01-01T00:00:00Z")!;
    const cases = fixture["cases"] as Case[];

    describe(file, () => {
      expect(cases.length).toBeGreaterThan(0);

      for (const [index, scenario] of cases.entries()) {
        it(`case ${index}`, () => {
          const condition = readCondition(scenario.condition);
          const actual = evaluateCondition(condition, scenario.record, actorId, now);

          const detail =
            `${file} case ${index}` +
            (scenario.why ? `\n  ${scenario.why}` : "") +
            `\n  condition: ${JSON.stringify(scenario.condition)}` +
            `\n  record:    ${JSON.stringify(scenario.record)}`;

          expect(actual, detail).toBe(scenario.expect);
        });
      }
    });
  }
});
