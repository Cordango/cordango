// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

/** One role's opinion about one field: an absent side means "this role said nothing, use the
 * entity-level default". */
export type FieldOverride = {
  field: string;
  read?: boolean;
  update?: boolean;
};

/** What one role may do to one entity. `entity` is a key, or `"*"` for the catch-all a role falls
 * back to when it names no grant for the entity in hand. */
export type EntityGrant = {
  entity: string;
  create?: boolean;
  read?: boolean;
  update?: boolean;
  delete?: boolean;
  fieldOverrides?: FieldOverride[];
  /** Command keys this grant unlocks. Deny-by-default: a command absent from every one of the
   * caller's grants cannot be run, even by a role that may update the entity freely. `"*"` means
   * all of them. */
  commands?: string[];
};

/** A named role and the grants it carries. */
export type RoleDefinition = {
  key: string;
  grants: EntityGrant[];
};

/**
 * The application's `roles` block, as typed data rather than JSON. The generator emits one instance
 * of this from the compiled definition, so the rules are compiled into the application.
 */
export type AppPermissions = {
  roles: RoleDefinition[];
};

/** An application whose definition declares no roles at all. */
export const noPermissions: AppPermissions = { roles: [] };

/** True when the definition declared no roles. Such an application has decided nothing about
 * access, and the resolver answers read-only rather than inventing a permission model for it. */
export function declaresNoRoles(permissions: AppPermissions): boolean {
  return permissions.roles.length === 0;
}

/** Who is asking, as the request authenticated them. */
export type CurrentUser = {
  /** Null when nobody is signed in. */
  userId: string | null;
  isAdministrator: boolean;
  roleKeys: readonly string[];
};
