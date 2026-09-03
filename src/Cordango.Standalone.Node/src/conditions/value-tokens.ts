// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Instant, PlainDate } from "../calc/dates.js";

/**
 * The placeholders a definition may write where a value goes: who is asking, and when.
 *
 * `{{actor.id}}`, `{{today}}`, `{{now}}`, and the offsets `{{today+7}}`, `{{today-30d}}`,
 * `{{today+2w}}`, `{{now-4h}}`. They appear in a command's `set`, in a workflow's effects, in a
 * condition's `value` and in a notification's text, which is why they resolve in one place rather
 * than four.
 *
 * This grammar has to match the compiler's `ExprTokens` — the `{{today+1w}}`-resolved-to-its-own-
 * literal-text failure is documented at length on the C# twin — and it has to match the C# runtime's
 * `ValueTokens`, which is what the shared condition fixtures hold it to.
 *
 * Everything comes from the clock that was passed in. Nothing here reads the machine's, so a test
 * can ask what a workflow would have done last March, and two applications processing the same
 * record at the same instant agree.
 */

const placeholder = /\{\{([^}]+)\}\}/g;

/** The same shape the compiler's `ExprTokens.Relative` matches. */
const relative = /^(today|now)(?:\s*([+-])\s*(\d{1,5})\s*([dwh])?)?$/;

/** A whole value that is nothing but one token: `"{{today}}"`. Returns the input unchanged when it
 * is not, or when nothing answers the token — a mistyped one stays visible. */
export function resolveValue(
  value: string | null | undefined,
  actorId: string | null,
  userId: string | null,
  now: Instant,
): string | null {
  if (value === null || value === undefined) return null;
  if (value.length < 5 || !value.startsWith("{{") || !value.endsWith("}}")) return value;

  return token(value.slice(2, -2).trim(), actorId, userId, now) ?? value;
}

/** Every token inside a longer string: `"Due {{today+7}}, raised by {{actor.id}}"`. A token nothing
 * answers is left as written rather than blanked, so a mistyped one is visible in the output instead
 * of turning into a silent gap. */
export function fillTokens(
  template: string | null | undefined,
  actorId: string | null,
  userId: string | null,
  now: Instant,
  readers?: {
    /** A field of the record the rule is about, for `{{record.x}}`. */
    record?: (field: string) => string | null;
    /** A field of the row being iterated, for `{{source.x}}` inside a `createForEach`. */
    source?: (field: string) => string | null;
    /** A field of the record an earlier effect in the same list just inserted, for `{{created.id}}`. */
    created?: (field: string) => string | null;
  },
): string | null {
  if (template === null || template === undefined) return null;

  return template.replace(placeholder, (whole, inner: string) => {
    const name = inner.trim();

    if (readers?.record && name.startsWith("record.")) return readers.record(name.slice("record.".length)) ?? "";
    if (readers?.source && name.startsWith("source.")) return readers.source(name.slice("source.".length)) ?? "";
    if (readers?.created && name.startsWith("created.")) return readers.created(name.slice("created.".length)) ?? "";

    return token(name, actorId, userId, now) ?? whole;
  });
}

function token(name: string, actorId: string | null, userId: string | null, now: Instant): string | null {
  // Both spellings, because both are in the corpus and neither is wrong.
  if (name === "actor.id" || name === "currentUser.id") return actorId ?? "";
  if (name === "actor.userId" || name === "currentUser.userId") return userId ?? "";

  const match = relative.exec(name);
  if (!match) return null;

  const anchor = match[1]!;
  const unit = match[4] ?? "";

  // An hour offset on a date anchor would resolve to the day it started on: the author meant
  // {{now-4h}}. The gate refuses that pairing, and leaving it unresolved here rather than quietly
  // agreeing is what keeps the two answers the same.
  if (anchor === "today" && unit === "h") return null;

  let amount = 0;
  if (match[2] !== undefined) {
    amount = Number(match[3]);
    if (match[2] === "-") amount = -amount;
  }

  if (anchor === "today") {
    // A date anchor is a DATE — midnight of the day the clock is on, in UTC.
    const days = unit === "w" ? amount * 7 : amount;
    return now.utcDate().addDays(days).toString();
  }

  const shifted = new Instant(
    now.epochMicros +
      BigInt(amount) * (unit === "w" ? 604_800_000_000n : unit === "h" ? 3_600_000_000n : 86_400_000_000n),
  );
  return roundTrip(shifted);
}

/** C#'s round-trip ("o") format: seven fractional digits, always, and an explicit offset. The
 * resolved text can meet a stored value in an ordinal comparison, so the spelling is part of the
 * contract, not a style. */
function roundTrip(at: Instant): string {
  const date = at.utcDate();
  const dayMicros = at.epochMicros - at.utcDate().atMidnightUtc().epochMicros;
  const seconds = dayMicros / 1_000_000n;
  const micros = dayMicros % 1_000_000n;

  const hh = String(seconds / 3600n).padStart(2, "0");
  const mm = String((seconds / 60n) % 60n).padStart(2, "0");
  const ss = String(seconds % 60n).padStart(2, "0");
  const frac = String(micros).padStart(6, "0") + "0";

  return `${date.toString()}T${hh}:${mm}:${ss}.${frac}+00:00`;
}

export { PlainDate, Instant };
