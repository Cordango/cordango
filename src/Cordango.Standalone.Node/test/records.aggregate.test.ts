// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import { Dec, PlainDate, RecordDescriptor, aggregate, type RecordRow } from "../src/index.js";

type Expense = RecordRow & {
  category: string | null;
  amount: Dec | null;
  spent_on: PlainDate | null;
};

const descriptor = new RecordDescriptor<Expense>("expense", "Expense", [
  { key: "category", type: "text" },
  { key: "amount", type: "money" },
  { key: "spent_on", type: "date" },
]);

function expense(id: string, category: string | null, amount: number | null, spentOn: string | null): Expense {
  return {
    id,
    category,
    amount: amount === null ? null : Dec.from(amount),
    spent_on: spentOn === null ? null : PlainDate.parse(spentOn),
  };
}

const rows = [
  expense("1", "travel", 100, "2026-01-05"),
  expense("2", "travel", 250, "2026-02-10"),
  expense("3", "travel", null, "2026-02-20"),
  expense("4", "software", 80, "2026-02-01"),
  expense("5", null, 40, "2026-03-15"),
];

describe("aggregate", () => {
  it("counts without a field", () => {
    expect(aggregate(rows, descriptor, "count", null, null)).toEqual({
      op: "count",
      buckets: [{ key: null, value: 5 }],
    });
  });

  it("sums, averages and finds the extrema of the rows that have a value", () => {
    expect(aggregate(rows, descriptor, "sum", "amount", null).buckets).toEqual([{ key: null, value: 470 }]);
    expect(aggregate(rows, descriptor, "avg", "amount", null).buckets).toEqual([{ key: null, value: 117.5 }]);
    expect(aggregate(rows, descriptor, "min", "amount", null).buckets).toEqual([{ key: null, value: 40 }]);
    expect(aggregate(rows, descriptor, "max", "amount", null).buckets).toEqual([{ key: null, value: 250 }]);
  });

  it("answers null when there was nothing to average", () => {
    const empty = aggregate([], descriptor, "avg", "amount", null);
    expect(empty.buckets).toEqual([{ key: null, value: null }]);
  });

  it("groups by a text field, with blanks as the empty group", () => {
    const result = aggregate(rows, descriptor, "sum", "amount", "category");
    expect(result.buckets).toEqual([
      { key: "", value: 40 },
      { key: "software", value: 80 },
      { key: "travel", value: 350 },
    ]);
  });

  it("groups by the month of a date field and drops rows with no date", () => {
    const result = aggregate(rows, descriptor, "sum", "amount", "month_of:spent_on");
    expect(result.buckets).toEqual([
      { key: "2026-01", value: 100 },
      { key: "2026-02", value: 330 },
      { key: "2026-03", value: 40 },
    ]);
  });

  it("refuses an unknown operation, a missing field and a non-numeric field", () => {
    expect(() => aggregate(rows, descriptor, "median", "amount", null)).toThrowError(
      expect.objectContaining({ code: "aggregate.operation_unknown" }),
    );
    expect(() => aggregate(rows, descriptor, "sum", null, null)).toThrowError(
      expect.objectContaining({ code: "aggregate.field_required" }),
    );
    expect(() => aggregate(rows, descriptor, "sum", "category", null)).toThrowError(
      expect.objectContaining({ code: "aggregate.field_type" }),
    );
  });

  it("refuses month grouping on a field that is not a date", () => {
    expect(() => aggregate(rows, descriptor, "count", null, "month_of:category")).toThrowError(
      expect.objectContaining({ code: "aggregate.group_type" }),
    );
  });

  it("refuses a field the entity does not have", () => {
    expect(() => aggregate(rows, descriptor, "sum", "nope", null)).toThrowError(
      expect.objectContaining({ code: "aggregate.field_unknown" }),
    );
  });
});
