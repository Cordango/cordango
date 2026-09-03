// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { EntityAccess, type FieldRule } from "./entity-access.js";
import { declaresNoRoles, type AppPermissions, type CurrentUser, type EntityGrant, type FieldOverride, type RoleDefinition } from "./permissions.js";

/**
 * Turns the application's roles and the caller's role keys into an `EntityAccess`.
 *
 * A port of the C# runtime's resolver, rule for rule; Cordango Platform enforces the same four rules
 * with its own code and all three are held together by `tests/fixtures/permissions/` — decision
 * fixtures asserted by every suite, so drift becomes a red test rather than a wrong answer in
 * production.
 *
 * The four rules, in the order they matter:
 * 1. Roles union. Any role of the caller's that allows an operation allows it.
 * 2. Within one role, a grant naming the entity beats that role's `*` wildcard.
 * 3. A field's answer is that role's explicit override if it has one, else that role's entity-level
 *    default — and only then unioned across roles.
 * 4. Commands are deny-by-default and are never implied by update.
 */

/** What this caller may do to this entity. */
export function resolveAccess(permissions: AppPermissions, user: CurrentUser, entityKey: string): EntityAccess {
  if (user.isAdministrator) return EntityAccess.full;

  // Nobody signed in gets nothing, and this line comes BEFORE the no-roles default rather than
  // after it: an application whose definition declares no roles falls back to read-only, which is a
  // reasonable answer for a colleague and a terrible one for the internet.
  if (user.userId === null) return EntityAccess.none;

  if (declaresNoRoles(permissions)) return EntityAccess.readOnly;
  return resolveRoles(permissions, user.roleKeys, entityKey);
}

/** The rule evaluation itself, over role keys alone. Separate from the caller so the decision
 * fixtures can drive it directly, with no user object and no bypass in the way. */
export function resolveRoles(
  permissions: AppPermissions,
  roleKeys: readonly string[],
  entityKey: string,
): EntityAccess {
  if (roleKeys.length === 0) return EntityAccess.none;

  const wanted = new Set(roleKeys);

  // Rule 2, applied per role: the grant naming the entity, else the wildcard, else this role has
  // nothing to say here and drops out entirely.
  const grants: EntityGrant[] = [];
  for (const role of permissions.roles) {
    if (!wanted.has(role.key)) continue;
    const grant = effectiveGrant(role, entityKey);
    if (grant !== null) grants.push(grant);
  }

  if (grants.length === 0) return EntityAccess.none;

  // Rule 1.
  const create = grants.some((g) => g.create === true);
  const read = grants.some((g) => g.read === true);
  const update = grants.some((g) => g.update === true);
  const del = grants.some((g) => g.delete === true);

  // Rule 3. Note the shape: for EACH grant, resolve the field against THAT grant's default before
  // unioning. Unioning the defaults first and then applying overrides would let a role that may
  // read everything silently re-open a field another role hid.
  const fields = new Map<string, FieldRule>();
  const named = new Set<string>();
  for (const grant of grants) for (const override of grant.fieldOverrides ?? []) named.add(override.field);

  for (const field of named) {
    const fieldRead = grants.some((g) => findOverride(g, field)?.read ?? (g.read === true));
    const fieldUpdate = grants.some((g) => findOverride(g, field)?.update ?? (g.update === true));
    fields.set(field, { read: fieldRead, update: fieldUpdate });
  }

  // Rule 4.
  const allCommands = grants.some((g) => (g.commands ?? []).includes("*"));
  const commands = new Set(grants.flatMap((g) => g.commands ?? []).filter((c) => c !== "*"));

  return new EntityAccess(create, read, update, del, fields, commands, allCommands);
}

function effectiveGrant(role: RoleDefinition, entityKey: string): EntityGrant | null {
  let specific: EntityGrant | null = null;
  let wildcard: EntityGrant | null = null;
  for (const grant of role.grants) {
    if (grant.entity === entityKey) specific = grant;
    else if (grant.entity === "*") wildcard ??= grant;
  }
  return specific ?? wildcard;
}

function findOverride(grant: EntityGrant, field: string): FieldOverride | null {
  for (const override of grant.fieldOverrides ?? []) if (override.field === field) return override;
  return null;
}
