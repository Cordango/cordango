// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { describe, expect, it } from "vitest";
import {
  PlainDate,
  RecordDescriptor,
  applyQuery,
  parseFilters,
  parseSort,
  type RecordRow,
} from "../src/index.js";

type Task = RecordRow & { title: string | null; due_on: PlainDate | null; done: boolean };

const descriptor = new RecordDescriptor<Task>("task", "Task", [
  { key: "id", type: "id" },
  { key: "title", type: "text" },
  { key: "due_on", type: "date" },
  { key: "done", type: "boolean" },
]);

// Fixed ids, in the order the assertions expect. `applyQuery` appends an id ordering when nothing
// else says otherwise, so random ids would make every assertion about ORDER a coin toss.
const overdue: Task = { id: "1", title: "overdue", due_on: PlainDate.parse("2026-01-01"), done: false };
const soon: Task = { id: "2", title: "soon", due_on: PlainDate.parse("2026-06-15"), done: false };
const someday: Task = { id: "3", title: "someday", due_on: null, done: false };

function matching(...terms: string[]): string[] {
  return applyQuery([overdue, soon, someday], descriptor, parseFilters(terms), []).map((t) => t.title!);
}

describe("RecordQuery", () => {
  it.each([
    ["due_on:lt:2026-03-01", "overdue"],
    ["due_on:lte:2026-01-01", "overdue"],
    ["due_on:gt:2026-03-01", "soon"],
    ["due_on:gte:2026-06-15", "soon"],
  ])("a comparison against a column that may be empty is a query (%s)", (term, expected) => {
    expect(matching(term)).toEqual([expected]);
  });

  it("a row with no value matches no comparison", () => {
    expect(matching("due_on:lt:2030-01-01")).not.toContain("someday");
    expect(matching("due_on:gt:2000-01-01")).not.toContain("someday");
  });

  it("between takes both bounds and includes them", () => {
    expect(matching("due_on:between:2026-01-01|2026-06-15")).toEqual(["overdue", "soon"]);
    expect(matching("due_on:between:2026-02-01|2026-12-31")).toEqual(["soon"]);
    expect(matching("due_on:between:2027-01-01|2027-12-31")).toEqual([]);
  });

  it("between without two bounds says so", () => {
    expect(() => matching("due_on:between:2026-01-01")).toThrowError(
      expect.objectContaining({ code: "query.range_invalid" }),
    );
  });

  it("an unknown operator names the ones that exist", () => {
    expect(() => matching("due_on:sometime:2026-01-01")).toThrowError(
      expect.objectContaining({ code: "query.operator_unknown" }),
    );
    expect(() => matching("due_on:sometime:2026-01-01")).toThrowError(/between/);
  });

  it("in and notIn split on the pipe and an empty list is not a match-all", () => {
    expect(matching("title:in:overdue|soon")).toEqual(["overdue", "soon"]);
    expect(matching("title:notIn:overdue|soon")).toEqual(["someday"]);
    expect(matching("title:in:")).toEqual([]);
    expect(matching("title:notIn:")).toEqual(["overdue", "soon", "someday"]);
  });

  it("contains and startsWith compare text and refuse other types", () => {
    expect(matching("title:contains:over")).toEqual(["overdue"]);
    expect(matching("title:startsWith:soon")).toEqual(["soon"]);
    expect(() => matching("due_on:contains:2026")).toThrowError(
      expect.objectContaining({ code: "query.operator_type" }),
    );
  });

  it("isEmpty and isNotEmpty treat null and the empty string alike", () => {
    expect(matching("due_on:isEmpty:")).toEqual(["someday"]);
    expect(matching("due_on:isNotEmpty:")).toEqual(["overdue", "soon"]);
  });

  it("an unknown field is refused by name", () => {
    expect(() => matching("nope:eq:1")).toThrowError(
      expect.objectContaining({ code: "query.field_unknown" }),
    );
  });

  it("a value that does not parse against the column says so", () => {
    expect(() => matching("due_on:eq:not-a-date")).toThrowError(
      expect.objectContaining({ code: "query.value_invalid" }),
    );
  });

  it("a term that is not a filter says how to write one", () => {
    expect(() => parseFilters(["justakey"])).toThrowError(
      expect.objectContaining({ code: "query.filter_invalid" }),
    );
  });

  it("sorts ascending and descending, with the id as the final tiebreaker", () => {
    const rows = applyQuery([someday, soon, overdue], descriptor, [], parseSort("due_on"));
    expect(rows.map((t) => t.title)).toEqual(["overdue", "soon", "someday"]);

    const descending = applyQuery([someday, soon, overdue], descriptor, [], parseSort("-due_on"));
    expect(descending.map((t) => t.title)).toEqual(["someday", "soon", "overdue"]);

    const unsorted = applyQuery([someday, soon, overdue], descriptor, [], []);
    expect(unsorted.map((t) => t.title)).toEqual(["overdue", "soon", "someday"]);
  });
});
