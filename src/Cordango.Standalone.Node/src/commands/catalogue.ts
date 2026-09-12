// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { Condition } from "../conditions/condition.js";

/** A field a command writes itself, and the value it writes. The value is a literal, or one of
 * `{{now}}`, `{{today}}`, `{{actor.id}}`, `{{actor.userId}}`. */
export interface CommandSet {
  readonly field: string;
  readonly value: string | null;
}

/**
 * Telling somebody that a command ran.
 *
 * Every string is a template resolved against the record AFTER the command wrote to it, which is
 * what lets a message say what the record now IS rather than what it was.
 */
export interface CommandNotification {
  /** Who — usually `{{record.submitted_by}}`. */
  readonly to: string;
  readonly title: string;
  readonly message?: string | null;
  /** `auto` links to the record the command ran on. */
  readonly link?: string | null;
}

/** One effect a command or a workflow runs. The same shape in both, run by the same runner. */
export interface EffectDefinition {
  readonly type: string;
  readonly [option: string]: unknown;
}

/**
 * One thing a person can do to a record beyond editing its fields: approve it, close it, mark it
 * paid.
 *
 * Emitted from the definition as compiled data, so the rules a command enforces are the rules the
 * definition states — not a second copy of them written by hand in a route.
 */
export interface CommandDefinition {
  /** What the route names. */
  readonly key: string;
  /** What the button says. */
  readonly label: string;
  readonly entity: string;
  /** The field the process moves, when this command moves one. */
  readonly stateField?: string | null;
  /** The states the record may be in for this to be legal. Empty means the command does not move
   * the record and can run from any state. */
  readonly fromStates?: readonly string[];
  readonly toState?: string | null;
  /** Fields the caller supplies. */
  readonly inputFields?: readonly string[];
  /** Which of those may not be left blank. */
  readonly requiredInputFields?: readonly string[];
  /** Fields the command writes itself. */
  readonly sets?: readonly CommandSet[];
  readonly successMessage?: string | null;
  readonly notifications?: readonly CommandNotification[];
  /**
   * An extra condition the record must satisfy — a guard. Separate from the transition: a
   * transition says which STATES a command may run from, and a guard says everything else. "Do not
   * offboard somebody who already left" is not a state machine, it is one question about one field.
   */
  readonly when?: Condition | null;
  readonly effects?: readonly EffectDefinition[];
}

/** Every command the application declares, by entity. Generated. */
export class CommandCatalogue {
  static readonly empty = new CommandCatalogue([]);

  private readonly byEntity = new Map<string, CommandDefinition[]>();

  constructor(readonly commands: readonly CommandDefinition[]) {
    for (const command of commands) {
      const existing = this.byEntity.get(command.entity);
      if (existing === undefined) this.byEntity.set(command.entity, [command]);
      else existing.push(command);
    }
  }

  find(entity: string, key: string): CommandDefinition | undefined {
    return this.byEntity.get(entity)?.find((command) => command.key === key);
  }

  for(entity: string): readonly CommandDefinition[] {
    return this.byEntity.get(entity) ?? [];
  }
}
