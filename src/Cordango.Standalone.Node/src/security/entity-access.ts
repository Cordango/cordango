// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

/** How one field may be touched: `read` = it appears in responses, `update` = it may be set on
 * create and update. */
export type FieldRule = { read: boolean; update: boolean };

const emptyFields: ReadonlyMap<string, FieldRule> = new Map();
const emptyCommands: ReadonlySet<string> = new Set();

/**
 * One caller's effective access to one entity: the union of every role they hold, resolved down to
 * entity-level CRUD plus the fields that differ from it.
 *
 * Immutable and computed from data alone, so it can be built once per request and asked anything
 * without touching the database or the request context.
 */
export class EntityAccess {
  private readonly fields: ReadonlyMap<string, FieldRule>;
  private readonly commands: ReadonlySet<string>;
  private readonly allCommands: boolean;

  constructor(
    readonly create: boolean,
    readonly read: boolean,
    readonly update: boolean,
    readonly delete_: boolean,
    fields?: ReadonlyMap<string, FieldRule>,
    commands?: ReadonlySet<string>,
    allCommands = false,
  ) {
    this.fields = fields ?? emptyFields;
    this.commands = commands ?? emptyCommands;
    this.allCommands = allCommands;
  }

  /** An administrator: everything, including every command. */
  static readonly full = new EntityAccess(true, true, true, true, undefined, undefined, true);

  /** Nothing at all — no role of the caller's says anything about this entity. */
  static readonly none = new EntityAccess(false, false, false, false);

  /** What a caller gets on an application whose definition declares no roles: they may look, and
   * that is all. */
  static readonly readOnly = new EntityAccess(false, true, false, false);

  allows(operation: string): boolean {
    switch (operation) {
      case "create":
        return this.create;
      case "read":
        return this.read;
      case "update":
        return this.update;
      case "delete":
        return this.delete_;
      default:
        return false;
    }
  }

  /** A field with no override inherits the entity-level answer. */
  canReadField(field: string): boolean {
    return this.fields.get(field)?.read ?? this.read;
  }

  canUpdateField(field: string): boolean {
    return this.fields.get(field)?.update ?? this.update;
  }

  /** Fields to strip from responses. */
  get hiddenReadFields(): string[] {
    return [...this.fields.entries()].filter(([, rule]) => !rule.read).map(([field]) => field);
  }

  /** Fields a write may not touch. Keyed off explicit overrides only, so the restriction is the
   * exception, not the default. */
  get writeRestrictedFields(): string[] {
    return [...this.fields.entries()].filter(([, rule]) => !rule.update).map(([field]) => field);
  }

  /** Deny-by-default. True only for an administrator, or when some grant of the caller's names this
   * command (or `"*"`). */
  canRunCommand(commandKey: string): boolean {
    return this.allCommands || this.commands.has(commandKey);
  }

  /** The command keys allowed, for the client's own enablement decisions. `["*"]` when every
   * command is allowed. */
  get allowedCommands(): string[] {
    return this.allCommands ? ["*"] : [...this.commands].sort();
  }
}
