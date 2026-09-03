// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { EntityAccess } from "./entity-access.js";

/**
 * What a caller may SEE of a record — the one derivation of that question, for every path that hands
 * data back.
 *
 * A field hidden from a role has to stay hidden on every route that can carry it: the record itself,
 * the record a command hands back after running, and any message rendered from the record — a
 * `successMessage` containing `{{salary}}` leaks the salary in prose just as surely as the field
 * would. Stripping at each call site means the next route added inherits nothing.
 *
 * These are the CALLER-facing projections only. Workflows and effects run against the whole record:
 * a notification may legitimately quote a field its recipient could not have asked for, because it
 * goes to a configured destination rather than back down the wire to whoever made the request.
 */

/** True when this caller is hiding anything at all — the cheap test that lets administrators and
 * unrestricted roles skip the work entirely. */
export function restricts(access: EntityAccess): boolean {
  return access.hiddenReadFields.length > 0;
}

/**
 * The record as this caller may see it. Returns a new object rather than editing the one it was
 * given — the caller is usually holding the row something else is about to write.
 */
export function project(
  access: EntityAccess,
  record: Record<string, unknown> | null,
): Record<string, unknown> | null {
  if (record === null) return null;

  const hidden = access.hiddenReadFields;
  if (hidden.length === 0) return record;

  const clone: Record<string, unknown> = { ...record };
  for (const field of hidden) delete clone[field];
  return clone;
}

/** Every field of a write the caller was not allowed to set. Returned rather than thrown so the
 * caller can name all of them at once. */
export function rejectedWrites(access: EntityAccess, suppliedFields: Iterable<string>): string[] {
  const restricted = new Set(access.writeRestrictedFields);
  if (restricted.size === 0) return [];

  return [...new Set([...suppliedFields].filter((field) => restricted.has(field)))].sort();
}
