// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import {
  Dec,
  PlainDate,
  RecordDescriptor,
  computeAll,
  computedField,
  type RecordRow,
} from "../src/index.js";

const invoice = new RecordDescriptor<RecordRow>("invoice", "Invoice", [
  { key: "net", type: "money" },
  { key: "vat_rate", type: "decimal" },
  { key: "vat", type: "money" },
  { key: "gross", type: "money" },
  { key: "issued_on", type: "date" },
  { key: "due_on", type: "date" },
  { key: "days_to_pay", type: "integer" },
  { key: "large", type: "boolean" },
  { key: "band", type: "text" },
]);

describe("computedField", () => {
  it("works a figure out and writes it to the target field", () => {
    const vat = computedField(invoice, "vat", "net * vat_rate", { weekStartsMonday: true });
    const row: RecordRow = { id: "1", net: Dec.parse("100.00"), vat_rate: Dec.parse("0.19") };

    vat(row);

    expect((row["vat"] as Dec).toJSON()).toBe(19);
  });

  it("runs several in the order the generator worked out", () => {
    const compute = computeAll<RecordRow>([
      computedField(invoice, "vat", "net * vat_rate", { weekStartsMonday: true }),
      computedField(invoice, "gross", "net + vat", { weekStartsMonday: true }),
    ]);

    const row: RecordRow = { id: "1", net: Dec.parse("100.00"), vat_rate: Dec.parse("0.19") };
    compute(row);

    expect((row["gross"] as Dec).toJSON()).toBe(119);
  });

  it("reads a blank number as zero, the way the fixtures say", () => {
    const gross = computedField(invoice, "gross", "net + vat", { weekStartsMonday: true });
    const row: RecordRow = { id: "1", net: Dec.parse("50"), vat: null };

    gross(row);

    expect((row["gross"] as Dec).toJSON()).toBe(50);
  });

  it("writes null, not zero, when the answer is genuinely unknown", () => {
    // `days_between` over a date nobody has set has no answer, and a zero here would read as "paid
    // the same day" on every unissued invoice.
    const days = computedField(invoice, "days_to_pay", "days_between(issued_on, due_on)", {
      weekStartsMonday: true,
    });
    const row: RecordRow = { id: "1", issued_on: PlainDate.parse("2026-01-01"), due_on: null };

    days(row);

    expect(row["days_to_pay"]).toBeNull();
  });

  it("truncates toward zero for an integer target", () => {
    const days = computedField(invoice, "days_to_pay", "net / 7", { weekStartsMonday: true });
    const row: RecordRow = { id: "1", net: Dec.parse("100") };

    days(row);

    expect(row["days_to_pay"]).toBe(14);
  });

  it("answers a boolean target with a boolean", () => {
    const large = computedField(invoice, "large", "net > 1000", { weekStartsMonday: true });

    const big: RecordRow = { id: "1", net: Dec.parse("2000") };
    const small: RecordRow = { id: "2", net: Dec.parse("10") };

    large(big);
    large(small);

    expect(big["large"]).toBe(true);
    expect(small["large"]).toBe(false);
  });

  it("answers a text target through if()", () => {
    const band = computedField(invoice, "band", "if(net > 1000, 'large', 'small')", {
      weekStartsMonday: true,
    });
    const row: RecordRow = { id: "1", net: Dec.parse("2000") };

    band(row);

    expect(row["band"]).toBe("large");
  });

  it("refuses an expression that will not parse, when the application starts", () => {
    expect(() => computedField(invoice, "vat", "net * * rate", { weekStartsMonday: true })).toThrow(
      /could not be read/,
    );
  });

  it("refuses an expression naming a field the entity does not have", () => {
    expect(() => computedField(invoice, "vat", "net * nonexistent", { weekStartsMonday: true })).toThrow();
  });

  it("refuses to write to a field the entity does not have", () => {
    expect(() => computedField(invoice, "nowhere", "net", { weekStartsMonday: true })).toThrow(
      /has no field 'nowhere'/,
    );
  });
});
