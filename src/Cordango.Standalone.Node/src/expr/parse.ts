// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Dec } from "../calc/decimal.js";

/**
 * The computed-expression language: numeric arithmetic, comparisons that return booleans, boolean
 * logic, date durations and parts, `pow`, and the two-value bounds `min`/`max`.
 *
 * A port of the compiler's `ComputedExpr` — same tokens, same precedence, same typing rules — so
 * that an expression the gate accepted parses to the same tree here. There is ONE grammar; a second
 * one would be a second set of answers to `1 / 0`. What the compiler does not ship is an evaluator,
 * and that is `evaluate.ts`'s job; this file only builds the tree.
 *
 * Expressions are data, never handed to a language evaluator.
 */

export type Kind = "number" | "boolean" | "date";

export type Node =
  | { type: "number"; kind: "number"; value: Dec }
  | { type: "boolean"; kind: "boolean"; value: boolean }
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

    if (!isIdentifier(token)) {
      this.error = `unexpected '${token}'`;
      return null;
    }
    if (this.peek() === "(") return this.function(token);

    const kind = this.fieldKind(token);
    if (kind === null) {
      this.error = `'${token}' is not a numeric, boolean, or date field`;
      return null;
    }
    return { type: "field", kind, key: token };
  }

  private function(name: string): Node | null {
    if (
      !mathFuncs.has(name) &&
      !durationFuncs.has(name) &&
      !datePartFuncs.has(name) &&
      !dateBoundaryFuncs.has(name) &&
      name !== prevFunc
    ) {
      this.error = `'${name}' is not a known function`;
      return null;
    }
    this.pos++; // '('

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
    if (rule.ordered && (left.kind !== right.kind || left.kind === "boolean")) {
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
