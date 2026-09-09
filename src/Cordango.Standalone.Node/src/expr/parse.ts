// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Dec } from "../calc/decimal.js";

/**
 * The computed-expression language: numeric arithmetic, comparisons that return booleans, boolean
 * logic, date durations and parts, `pow`, the two-value bounds `min`/`max`, coded text compared for
 * equality, and the three branching forms — `if`, `switch` and `case`.
 *
 * A port of the compiler's `ComputedExpr` — same tokens, same precedence, same typing rules — so
 * that an expression the gate accepted parses to the same tree here. There is ONE grammar; a second
 * one would be a second set of answers to `1 / 0`. What the compiler does not ship is an evaluator,
 * and that is `evaluate.ts`'s job; this file only builds the tree.
 *
 * Expressions are data, never handed to a language evaluator.
 */

export type Kind = "number" | "boolean" | "date" | "text";

export type Node =
  | { type: "number"; kind: "number"; value: Dec }
  | { type: "boolean"; kind: "boolean"; value: boolean }
  | { type: "text"; kind: "text"; value: string }
  | { type: "conditional"; kind: Kind; condition: Node; whenTrue: Node; whenFalse: Node }
  | { type: "field"; kind: Kind; key: string }
  | { type: "unary"; kind: Kind; op: "-" | "not"; operand: Node }
  | { type: "binary"; kind: Kind; op: string; left: Node; right: Node }
  | { type: "function"; kind: "number"; name: "pow" | "min" | "max"; left: Node; right: Node }
  | { type: "duration"; kind: "number"; name: string; from: string; to: string }
  | { type: "datePart"; kind: "number"; name: string; field: string }
  | { type: "dateBoundary"; kind: "date"; name: string; field: string }
  | { type: "prev"; kind: "number"; field: string; seed: Node | null };

export const durationFuncs = new Set(["minutes_between", "hours_between", "days_between"]);

export const datePartFuncs = new Set([
  "weekday",
  "week_of_year",
  "month_of",
  "day_of_month",
  "day_of_year",
  "year_of",
  "hour_of",
]);

export const dateBoundaryFuncs = new Set(["start_of_week", "end_of_week", "start_of_month", "end_of_month"]);

export const mathFuncs = new Set(["pow", "min", "max"]);

export const prevFunc = "prev";

export const conditionalFunc = "if";

/** `switch(field, key, value, …, default)` — a table keyed on one field, folded into the
 * conditionals it means, so nothing past the parser learns a new shape. */
export const switchFunc = "switch";

/** `case(test, value, …, default)` — the first test that holds, folded the same way. */
export const caseFunc = "case";

/** Whether a token names a FUNCTION rather than a field. One list, because a name missing from it
 * is read as a field somebody meant to declare. */
export function isFunctionName(token: string): boolean {
  return (
    mathFuncs.has(token) ||
    durationFuncs.has(token) ||
    datePartFuncs.has(token) ||
    dateBoundaryFuncs.has(token) ||
    token === prevFunc ||
    token === conditionalFunc ||
    token === switchFunc ||
    token === caseFunc
  );
}

export const keywords = new Set(["true", "false", "and", "or", "not"]);

/** What type each identifier has. Null means "not a field an expression may read". */
export type FieldKind = (identifier: string) => Kind | null;

export type ParseResult = { node: Node; error: null } | { node: null; error: string };

/** Splits a reference hop into its two halves, or null for a plain local field. The one place that
 * knows the shape of a hop. */
export function hop(identifier: string): { reference: string; field: string } | null {
  const dot = identifier.indexOf(".");
  if (dot <= 0 || dot === identifier.length - 1) return null;
  return { reference: identifier.slice(0, dot), field: identifier.slice(dot + 1) };
}

export function parse(expr: string | null | undefined, fieldKind: FieldKind): ParseResult {
  const tokens = tokenize(expr);
  if (tokens.error !== null) return { node: null, error: tokens.error };

  const parser = new Parser(tokens.tokens, fieldKind);
  const node = parser.parse();
  return parser.error !== null || node === null
    ? { node: null, error: parser.error ?? "expression is empty" }
    : { node, error: null };
}

class Parser {
  private pos = 0;
  error: string | null = null;

  constructor(
    private readonly tokens: string[],
    private readonly fieldKind: FieldKind,
  ) {}

  parse(): Node | null {
    const node = this.or();
    if (this.error === null && this.pos < this.tokens.length) this.error = `unexpected '${this.tokens[this.pos]}'`;
    return this.error === null ? node : null;
  }

  private or(): Node | null {
    let left = this.and();
    while (this.error === null && this.take("or")) left = this.binary("or", left, this.and(), "boolean", { booleans: true });
    return left;
  }

  private and(): Node | null {
    let left = this.equality();
    while (this.error === null && this.take("and"))
      left = this.binary("and", left, this.equality(), "boolean", { booleans: true });
    return left;
  }

  private equality(): Node | null {
    let left = this.comparison();
    while (this.error === null && (this.peek() === "==" || this.peek() === "!=")) {
      const op = this.tokens[this.pos++]!;
      const right = this.comparison();
      if (left !== null && right !== null && left.kind !== right.kind)
        this.error = `operator '${op}' cannot compare a ${left.kind} with a ${right.kind}`;
      else left = this.binary(op, left, right, "boolean", { sameKind: true });
    }
    return left;
  }

  private comparison(): Node | null {
    let left = this.additive();
    while (this.error === null && ["<", "<=", ">", ">="].includes(this.peek() ?? "")) {
      const op = this.tokens[this.pos++]!;
      left = this.binary(op, left, this.additive(), "boolean", { ordered: true });
    }
    return left;
  }

  private additive(): Node | null {
    let left = this.term();
    while (this.error === null && (this.peek() === "+" || this.peek() === "-")) {
      const op = this.tokens[this.pos++]!;
      left = this.binary(op, left, this.term(), "number", { numbers: true });
    }
    return left;
  }

  private term(): Node | null {
    let left = this.unary();
    while (this.error === null && (this.peek() === "*" || this.peek() === "/")) {
      const op = this.tokens[this.pos++]!;
      left = this.binary(op, left, this.unary(), "number", { numbers: true });
    }
    return left;
  }

  private unary(): Node | null {
    if (this.take("-")) {
      const operand = this.unary();
      if (operand !== null && operand.kind !== "number") this.error = "unary '-' requires a number";
      return operand === null ? null : { type: "unary", kind: "number", op: "-", operand };
    }
    if (this.take("not") || this.take("!")) {
      const operand = this.unary();
      if (operand !== null && operand.kind !== "boolean") this.error = "'not' requires a boolean";
      return operand === null ? null : { type: "unary", kind: "boolean", op: "not", operand };
    }
    return this.primary();
  }

  private primary(): Node | null {
    if (this.error !== null) return null;
    if (this.pos >= this.tokens.length) {
      this.error = "expression ends unexpectedly";
      return null;
    }

    const token = this.tokens[this.pos++]!;
    if (token === "(") {
      const inner = this.or();
      if (!this.take(")")) this.error ??= "missing closing parenthesis";
      return inner;
    }
    if (token === "true") return { type: "boolean", kind: "boolean", value: true };
    if (token === "false") return { type: "boolean", kind: "boolean", value: false };

    if (/^\d/.test(token)) {
      const value = Dec.parse(token);
      if (value === null) {
        this.error = `'${token}' isn't a number`;
        return null;
      }
      return { type: "number", kind: "number", value };
    }

    if (isTextLiteral(token)) return { type: "text", kind: "text", value: token.slice(1, -1) };

    if (!isIdentifier(token)) {
      this.error = `unexpected '${token}'`;
      return null;
    }
    if (this.peek() === "(") return this.function(token);

    const kind = this.fieldKind(token);
    if (kind === null) {
      this.error = `'${token}' is not a numeric, boolean, date, or text field`;
      return null;
    }
    return { type: "field", kind, key: token };
  }

  private function(name: string): Node | null {
    if (!isFunctionName(name)) {
      this.error = `'${name}' is not a known function`;
      return null;
    }
    this.pos++; // '('

    if (name === conditionalFunc) {
      const test = this.or();
      if (!this.take(",")) {
        this.error ??= conditionalArity(name);
        return null;
      }
      const whenTrue = this.or();
      if (!this.take(",")) {
        this.error ??= conditionalArity(name);
        return null;
      }
      const whenFalse = this.or();
      if (this.peek() === ",") {
        this.error = conditionalArity(name);
        return null;
      }
      if (!this.take(")")) {
        this.error ??= `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      if (test === null || whenTrue === null || whenFalse === null) return null;
      if (test.kind !== "boolean") {
        this.error = `'${name}' tests something true or false, not a ${test.kind}`;
        return null;
      }
      if (whenTrue.kind !== whenFalse.kind) {
        this.error =
          `'${name}' must answer the same kind of thing either way, ` +
          `not a ${whenTrue.kind} and a ${whenFalse.kind}`;
        return null;
      }
      return { type: "conditional", kind: whenTrue.kind, condition: test, whenTrue, whenFalse };
    }

    if (name === switchFunc || name === caseFunc) {
      const rows: Node[] = [];
      for (;;) {
        const arg = this.or();
        if (arg === null) {
          this.error ??= `'${name}(' is missing its closing parenthesis`;
          return null;
        }
        rows.push(arg);
        if (this.take(",")) continue;
        break;
      }
      if (!this.take(")")) {
        this.error ??= `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      return name === switchFunc ? this.switchTable(rows) : this.caseLadder(rows);
    }

    if (name === prevFunc) {
      // The first argument is a FIELD NAME, not a value — `prev` reads that field on the previous
      // row, so passing an expression would be meaningless.
      const target = this.peek();
      if (target === null || !isIdentifier(target) || keywords.has(target)) {
        this.error = `'${name}' takes a field name, not '${target ?? ")"}'`;
        return null;
      }
      this.pos++;
      if (this.fieldKind(target) !== "number") {
        this.error = `'${name}(${target})' needs a numeric field`;
        return null;
      }

      let seed: Node | null = null;
      if (this.take(",")) {
        seed = this.or();
        if (seed === null) return null;
        if (seed.kind !== "number") {
          this.error = `'${name}' takes a number as its fallback`;
          return null;
        }
      }
      if (this.peek() === ",") {
        this.error = `'${name}' takes at most two arguments`;
        return null;
      }
      if (!this.take(")")) {
        this.error ??= `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      return { type: "prev", kind: "number", field: target, seed };
    }

    if (mathFuncs.has(name)) {
      const left = this.or();
      if (!this.take(",")) {
        this.error ??= `'${name}' takes exactly two arguments`;
        return null;
      }
      const right = this.or();
      if (this.peek() === ",") {
        this.error = `'${name}' takes exactly two arguments`;
        return null;
      }
      if (!this.take(")")) {
        this.error ??= `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      if (left === null || right === null) return null;
      if (left.kind !== "number" || right.kind !== "number") {
        this.error = `'${name}' takes two numbers`;
        return null;
      }
      return { type: "function", kind: "number", name: name as "pow" | "min" | "max", left, right };
    }

    if (datePartFuncs.has(name) || dateBoundaryFuncs.has(name)) {
      const field = this.peek();
      if (field === null || !isIdentifier(field) || keywords.has(field)) {
        this.error = `'${name}' takes a date field, not '${field ?? ")"}'`;
        return null;
      }
      this.pos++;
      if (this.fieldKind(field) !== "date") {
        this.error = `'${name}' takes a date field`;
        return null;
      }
      if (this.peek() === ",") {
        this.error = `'${name}' takes exactly one date field`;
        return null;
      }
      if (!this.take(")")) {
        this.error ??= `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      return dateBoundaryFuncs.has(name)
        ? { type: "dateBoundary", kind: "date", name, field }
        : { type: "datePart", kind: "number", name, field };
    }

    const args: string[] = [];
    while (this.error === null) {
      if (this.pos >= this.tokens.length) {
        this.error = `'${name}(' is missing its closing parenthesis`;
        return null;
      }
      const arg = this.tokens[this.pos]!;
      if (!isIdentifier(arg)) {
        this.error = `'${name}' takes date fields, not '${arg}'`;
        return null;
      }
      this.pos++;
      if (this.fieldKind(arg) !== "date") {
        this.error = `'${name}' takes date fields`;
        return null;
      }
      args.push(arg);
      if (this.take(",")) continue;
      break;
    }
    if (!this.take(")")) {
      this.error ??= `'${name}(' is missing its closing parenthesis`;
      return null;
    }
    if (args.length !== 2) {
      this.error = `'${name}' takes exactly two date fields`;
      return null;
    }
    return { type: "duration", kind: "number", name, from: args[0]!, to: args[1]! };
  }

  /**
   * `switch`, folded into the conditionals it means.
   *
   * Option checking is deliberately absent — which codes a select offers is the compiler's question,
   * asked once at author time, and a definition reaching this runtime has already been through it.
   * Everything that decides an ANSWER is here, and matches the compiler exactly.
   */
  private switchTable(rows: Node[]): Node | null {
    if (rows.length < 4 || rows.length % 2 !== 0) {
      this.error =
        `'${switchFunc}' takes a field, then a key and a value for each row of the table, then a ` +
        `default — an even number of arguments, and at least four`;
      return null;
    }
    const subject = rows[0]!;
    if (subject.type !== "field") {
      this.error = `'${switchFunc}' looks a value up ON something, so its first argument is a field, not a working-out`;
      return null;
    }
    if (subject.kind !== "text" && subject.kind !== "number") {
      this.error = `'${switchFunc}' keys a table on a code or a number, not a ${subject.kind}`;
      return null;
    }

    const pairs = (rows.length - 2) / 2;
    const keys: Node[] = [];
    const values: Node[] = [];
    const seenText = new Set<string>();
    const seenNumbers: Dec[] = [];

    for (let i = 0; i < pairs; i++) {
      const key = rows[1 + i * 2]!;
      if (key.type !== "text" && key.type !== "number") {
        this.error =
          `'${switchFunc}' keys are written out, not worked out — a key that has to be computed ` +
          `cannot be checked against the field's options`;
        return null;
      }
      if (key.kind !== subject.kind) {
        this.error =
          `'${switchFunc}' keys '${subject.key}', which is a ${subject.kind}, so its keys are ` +
          `${subject.kind}s and not ${key.kind}s`;
        return null;
      }

      const fresh =
        key.type === "text" ? !seenText.has(key.value) : !seenNumbers.some((seen) => seen.eq(key.value));
      if (!fresh) {
        const shown = key.type === "text" ? `'${key.value}'` : key.value.toString();
        this.error = `'${switchFunc}' repeats the key ${shown}, so the second one can never be reached`;
        return null;
      }
      if (key.type === "text") seenText.add(key.value);
      else seenNumbers.push(key.value);

      keys.push(key);
      values.push(rows[2 + i * 2]!);
    }

    const fallback = rows[rows.length - 1]!;
    const mismatch = answers(switchFunc, values, fallback);
    if (mismatch !== null) {
      this.error = mismatch;
      return null;
    }

    let node: Node = fallback;
    for (let i = pairs - 1; i >= 0; i--)
      node = {
        type: "conditional",
        kind: fallback.kind,
        condition: { type: "binary", kind: "boolean", op: "==", left: subject, right: keys[i]! },
        whenTrue: values[i]!,
        whenFalse: node,
      };
    return node;
  }

  /** The first test that holds, folded the same way `switchTable` is. */
  private caseLadder(rows: Node[]): Node | null {
    if (rows.length < 3 || rows.length % 2 === 0) {
      this.error =
        `'${caseFunc}' takes a test and a value for each row, then a default — an odd number of ` +
        `arguments, and at least three`;
      return null;
    }

    const pairs = (rows.length - 1) / 2;
    const tests: Node[] = [];
    const values: Node[] = [];
    for (let i = 0; i < pairs; i++) {
      const test = rows[i * 2]!;
      if (test.kind !== "boolean") {
        this.error = `'${caseFunc}' tests something true or false, not a ${test.kind}`;
        return null;
      }
      tests.push(test);
      values.push(rows[i * 2 + 1]!);
    }

    const fallback = rows[rows.length - 1]!;
    const mismatch = answers(caseFunc, values, fallback);
    if (mismatch !== null) {
      this.error = mismatch;
      return null;
    }

    let node: Node = fallback;
    for (let i = pairs - 1; i >= 0; i--)
      node = {
        type: "conditional",
        kind: fallback.kind,
        condition: tests[i]!,
        whenTrue: values[i]!,
        whenFalse: node,
      };
    return node;
  }

  private binary(
    op: string,
    left: Node | null,
    right: Node | null,
    result: Kind,
    rule: { numbers?: boolean; sameKind?: boolean; ordered?: boolean; booleans?: boolean },
  ): Node | null {
    if (left === null || right === null) return null;
    if (rule.numbers && (left.kind !== "number" || right.kind !== "number")) {
      this.error = `operator '${op}' requires numbers`;
      return null;
    }
    if (rule.ordered && (left.kind !== right.kind || (left.kind !== "number" && left.kind !== "date"))) {
      this.error = `operator '${op}' requires two numbers or two dates`;
      return null;
    }
    if (rule.booleans && (left.kind !== "boolean" || right.kind !== "boolean")) {
      this.error = `operator '${op}' requires booleans`;
      return null;
    }
    return { type: "binary", kind: result, op, left, right };
  }

  private peek(): string | null {
    return this.pos < this.tokens.length ? (this.tokens[this.pos] ?? null) : null;
  }

  private take(token: string): boolean {
    if (this.peek() !== token) return false;
    this.pos++;
    return true;
  }
}

function tokenize(expr: string | null | undefined): { tokens: string[]; error: string | null } {
  const source = (expr ?? "").trim();
  const tokens: string[] = [];
  if (source.length === 0) return { tokens, error: "expression is empty" };

  for (let i = 0; i < source.length; ) {
    const c = source[i]!;
    if (/\s/.test(c)) {
      i++;
      continue;
    }
    const two = source.slice(i, i + 2);
    if (two === "<=" || two === ">=" || two === "==" || two === "!=") {
      tokens.push(two);
      i += 2;
      continue;
    }
    if ("()+-*/!,<>".includes(c)) {
      tokens.push(c);
      i++;
      continue;
    }
    // A TEXT LITERAL, kept whole — quotes included — so it stays distinguishable from an identifier
    // by its first character alone. Either quote, and no escapes: a value needing one is written with
    // the other, and a value needing both is not a select code. Codes are what this compares.
    if (c === "'" || c === '"') {
      const close = source.indexOf(c, i + 1);
      if (close < 0) return { tokens, error: `a text value opened with ${c} is never closed` };
      const inner = source.slice(i + 1, close);
      if (inner.includes("\n") || inner.includes("\r"))
        return { tokens, error: "a text value cannot span lines" };
      tokens.push(source.slice(i, close + 1));
      i = close + 1;
      continue;
    }
    if (c === "=") return { tokens, error: "'=' isn't valid in an expression; use '==' for equality" };
    if (/[0-9]/.test(c)) {
      let j = i;
      while (j < source.length && /[0-9.]/.test(source[j]!)) j++;
      tokens.push(source.slice(i, j));
      i = j;
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      let j = i;
      while (j < source.length && /[A-Za-z0-9_]/.test(source[j]!)) j++;
      // ONE dot hop: `scenario.price_per_user` reads a field on the record this one references,
      // as a single token. The digit branch above already claimed `1.5`.
      if (j < source.length && source[j] === "." && j + 1 < source.length && /[A-Za-z_]/.test(source[j + 1]!)) {
        let k = j + 1;
        while (k < source.length && /[A-Za-z0-9_]/.test(source[k]!)) k++;
        tokens.push(source.slice(i, k));
        i = k;
        continue;
      }
      tokens.push(source.slice(i, j));
      i = j;
      continue;
    }
    return { tokens, error: `'${c}' isn't valid in an expression` };
  }
  return { tokens, error: null };
}

function isIdentifier(token: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_.]*$/.test(token);
}

/** A token the tokenizer kept its quotes on. Checked before `isIdentifier` everywhere, and mutually
 * exclusive with it by construction: no identifier starts with a quote. */
function isTextLiteral(token: string): boolean {
  return token.length >= 2 && (token[0] === "'" || token[0] === '"') && token[token.length - 1] === token[0];
}

function conditionalArity(name: string): string {
  return `'${name}' takes three arguments: a test, and one answer for each way it can go`;
}

/** Every way out answers the same kind, which is then the kind of the whole table. */
function answers(name: string, values: Node[], fallback: Node): string | null {
  for (const value of values)
    if (value.kind !== fallback.kind)
      return (
        `'${name}' must answer the same kind of thing every way it can go, ` +
        `not a ${value.kind} and a ${fallback.kind}`
      );
  return null;
}
