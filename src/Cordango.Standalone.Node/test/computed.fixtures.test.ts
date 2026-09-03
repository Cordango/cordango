// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import { Dec, PlainDate, evaluate, kindOf, parse, recordReader, type Value } from "../src/index.js";
import { fixtureFiles, loadFixture } from "./fixtures.js";

type FieldSpec = { type?: string };
type Case = { record: Record<string, unknown>; expr: string; expect: unknown; why?: string };

describe("computed expression fixtures", () => {
  expect(fixtureFiles("computed").length).toBeGreaterThanOrEqual(5);

  for (const file of fixtureFiles("computed")) {
    const fixture = loadFixture("computed", file);
    const fields = new Map(
      Object.entries((fixture["fields"] ?? {}) as Record<string, FieldSpec>).map(([key, spec]) => [
        key,
        spec.type ?? "decimal",
      ]),
    );
    const weekStartsMonday = fixture["weekStart"] !== "sunday";
    const cases = fixture["cases"] as Case[];

    describe(file, () => {
      expect(cases.length).toBeGreaterThan(0);

      for (const [index, scenario] of cases.entries()) {
        it(`case ${index}: ${scenario.expr}`, () => {
          const parsed = parse(scenario.expr, (identifier) => {
            const type = fields.get(identifier);
            return type === undefined ? null : kindOf(type);
          });
          expect(parsed.error, `\`${scenario.expr}\` does not parse: ${parsed.error}`).toBeNull();

          const value = evaluate(parsed.node!, {
            weekStartsMonday,
            read: recordReader(fields, scenario.record),
          });

          const detail =
            `${file} case ${index}` +
            (scenario.why ? `\n  ${scenario.why}` : "") +
            `\n  expr:   ${scenario.expr}` +
            `\n  record: ${JSON.stringify(scenario.record)}` +
            `\n  expect: ${JSON.stringify(scenario.expect)}, got: ${render(value)}`;

          if (scenario.expect === null) expect(value, detail).toBeNull();
          else if (typeof scenario.expect === "boolean") expect(value, detail).toBe(scenario.expect);
          else if (typeof scenario.expect === "number") {
            expect(value, detail).toBeInstanceOf(Dec);
            expect((value as Dec).eq(Dec.from(scenario.expect)!), detail).toBe(true);
          } else if (typeof scenario.expect === "string") {
            expect(value, detail).toBeInstanceOf(PlainDate);
            expect((value as PlainDate).toString(), detail).toBe(scenario.expect);
          } else expect.unreachable(detail);
        });
      }
    });
  }
});

function render(value: Value): string {
  if (value === null) return "null";
  return value.toString();
}
