// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { evaluateCondition, type ConditionRecord } from "../conditions/evaluator.js";
import { Dec } from "../calc/decimal.js";
import { Instant, PlainDate } from "../calc/dates.js";
import type { RecordRow } from "../records/descriptor.js";
import { RecordError } from "../records/errors.js";
import type { Clock } from "../records/hooks.js";
import { projected, type CommandResult, type CommandRunner } from "../records/gateway.js";
import type { RecordStoreApi } from "../records/store.js";
import type { EntityAccess } from "../security/entity-access.js";
import type { CurrentUser } from "../security/permissions.js";
import type { NotificationService } from "../notifications/notifications.js";
import type { CommandCatalogue, CommandDefinition } from "./catalogue.js";

/** Runs a command's effects. The workflow runner satisfies this; an application with no effects
 * passes nothing and the commands still run. */
export interface EffectRunner {
  runEffects(
    effects: readonly { readonly type: string; readonly [option: string]: unknown }[],
    because: string,
    entity: string,
    record: ConditionRecord,
  ): Promise<void>;
}

/** Who the actor is, in the two spellings a template may ask for. */
export interface Actor {
  readonly userId: string | null;
  /** The directory Person behind the login, when the login has one. `{{actor.id}}` means this. */
  readonly personId: string | null;
}

const placeholder = /\{\{([^}]+)\}\}/g;

/**
 * Running a command, in the order the checks have to happen.
 *
 * **Permission, then legality, then guard, then input, then write.** The order is not arbitrary. A
 * caller who may not run the command must be refused before they learn whether the record is in a
 * state that would have allowed it; a record in the wrong state must be refused before its fields
 * are touched; and required input must be checked before anything is saved, because a command that
 * half-ran is worse than one that did not run.
 *
 * The write goes through the ordinary store, so a command fires the same hooks an edit does. A
 * workflow watching for a field change does not need to know whether a person typed the value or a
 * command set it.
 */
export class CommandService<T extends RecordRow> implements CommandRunner<T> {
  constructor(
    private readonly store: RecordStoreApi<T>,
    private readonly catalogue: CommandCatalogue,
    private readonly actor: Actor,
    private readonly clock: Clock,
    private readonly notifications: NotificationService | null = null,
    private readonly effects: EffectRunner | null = null,
  ) {}

  async run(
    id: string,
    commandKey: string,
    input: unknown,
    access: EntityAccess,
  ): Promise<CommandResult> {
    const entity = this.store.descriptor.entityKey;

    const command = this.catalogue.find(entity, commandKey);
    if (command === undefined)
      throw new RecordError("command.unknown", `'${entity}' has no command '${commandKey}'.`, 404);

    // Deny-by-default, and checked first. Telling somebody the record is in the wrong state before
    // telling them they may not do this at all leaks the record's state to a caller who is not
    // allowed to ask.
    if (!access.canRunCommand(commandKey))
      throw RecordError.forbidden("command.denied", `Your role may not run '${command.label}'.`);

    const record = await this.store.find(id);
    if (record === undefined) throw RecordError.notFound(entity, id);

    const changes = new Map<string, string | null>();
    const snapshot = wire(record);

    const fromStates = command.fromStates ?? [];
    if (command.stateField != null && fromStates.length > 0) {
      const current = asText(snapshot[command.stateField]) ?? "";
      if (!fromStates.includes(current))
        throw new RecordError(
          "command.illegal_transition",
          `'${command.label}' cannot run on a record that is '${current}'.`,
          409,
        );
    }

    // The guard, after the transition and before anything is read from the caller.
    //
    // After the transition, because "this claim is already reimbursed" is a better answer than
    // "this claim does not qualify" and the state is the more specific fact. Before the input,
    // because a command that will not run should not be telling the caller which fields it would
    // have wanted.
    //
    // The message stays general on purpose. A guard may read a field the caller's role cannot, and
    // naming it in a refusal would be a way to read it.
    if (
      command.when != null
      && !evaluateCondition(command.when, snapshot, this.actor.personId, this.clock.utcNow)
    )
      throw new RecordError(
        "command.not_applicable",
        `'${command.label}' cannot run on this record.`,
        409,
      );

    const supplied = isPlainObject(input) ? input : {};
    const required = command.requiredInputFields ?? [];

    for (const field of command.inputFields ?? []) {
      const value = field in supplied ? asText(supplied[field]) : null;

      if (required.includes(field) && (value === null || value.trim() === ""))
        throw new RecordError("command.input_required", `'${command.label}' needs ${field}.`, 400, [
          field,
        ]);

      if (value !== null) changes.set(field, value);
    }

    for (const set of command.sets ?? []) changes.set(set.field, this.resolve(set.value));

    // The state move is applied LAST, so a set or an input naming the state field cannot override
    // where the process says the record goes.
    if (command.stateField != null && command.toState != null)
      changes.set(command.stateField, command.toState);

    for (const key of changes.keys())
      if (this.store.descriptor.tryGetField(key) === undefined)
        throw new RecordError(
          "command.field_unknown",
          `'${command.label}' writes '${key}', which '${entity}' does not have.`,
        );

    // Converted through the same path an ordinary PATCH of the same field takes. Two conversion
    // paths for one field is how a command ends up storing "2026-01-02T00:00:00" where an edit
    // stores "2026-01-02".
    const incoming = { ...record } as T;
    const target = incoming as Record<string, unknown>;
    for (const [key, value] of changes) {
      const field = this.store.descriptor.tryGetField(key);
      if (field !== undefined) target[key] = coerce(field.type, value);
    }
    incoming.id = record.id;

    const updated = await this.store.update(id, incoming, [...changes.keys()]);

    // After the write, so a message can say what the record now is — and so that a failure to
    // notify cannot roll back a decision somebody already made.
    await this.notify(command, updated);

    // The same, and for the same reason: an effect reads the record the command has already moved,
    // and an effect that fails does not undo the move. `approve` creating an invoice is the invoice
    // failing to appear, not the approval coming back.
    const effects = command.effects ?? [];
    if (effects.length > 0 && this.effects !== null)
      await this.effects.runEffects(effects, `Command '${command.key}'`, entity, wire(updated));

    // Projected AND converted, through the same helper the record routes use. A command answers
    // with a record, so it owes the caller the same shape a GET of that record would give them —
    // including the field hiding, which a command must not become a way around.
    return {
      record: projected(access, updated),
      message: command.successMessage ?? null,
    };
  }

  private async notify(command: CommandDefinition, record: T): Promise<void> {
    const notifications = command.notifications ?? [];
    if (notifications.length === 0 || this.notifications === null) return;

    const node = wire(record);

    for (const notification of notifications) {
      const link =
        notification.link === "auto"
          ? `/record/${this.store.descriptor.entityKey}/${record.id}`
          : this.render(notification.link ?? null, node);

      await this.notifications.send(
        this.render(notification.to, node),
        this.render(notification.title, node) ?? "",
        this.render(notification.message ?? null, node),
        link,
      );
    }
  }

  /**
   * Fill `{{record.field}}` and the actor tokens from the record as it now stands.
   *
   * Rendered from the FULL record, not from a caller-projected copy — a notification goes to a
   * configured recipient rather than back down the wire to whoever pressed the button, so it may
   * legitimately quote a field the caller could not read.
   */
  private render(template: string | null, record: ConditionRecord): string | null {
    if (template === null) return null;

    return template.replace(placeholder, (whole: string, inner: string) => {
      const token = inner.trim();

      if (token.startsWith("record."))
        return asText(record[token.slice("record.".length)]) ?? "";

      switch (token) {
        case "actor.id":
          return this.actor.personId ?? "";
        case "actor.userId":
          return this.actor.userId ?? "";
        case "now":
          return this.clock.utcNow.toString();
        case "today":
          return this.today();
        default:
          return whole;
      }
    });
  }

  /** Placeholders a definition may write in a command's `set`. */
  private resolve(value: string | null): string | null {
    switch (value) {
      case null:
        return null;
      case "{{now}}":
        return this.clock.utcNow.toString();
      case "{{today}}":
        return this.today();
      case "{{actor.id}}":
        return this.actor.personId;
      case "{{actor.userId}}":
        return this.actor.userId;
      default:
        return value;
    }
  }

  private today(): string {
    return this.clock.utcNow.toString().slice(0, 10);
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** A record as JSON holds it, which is what a condition and a template read. */
function wire(record: RecordRow): ConditionRecord {
  const result: ConditionRecord = {};
  for (const key of Object.keys(record)) {
    const value = record[key];
    result[key] =
      value instanceof Dec || value instanceof PlainDate || value instanceof Instant
        ? value.toString()
        : value;
  }
  return result;
}

function asText(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "string") return value;
  if (value instanceof Dec || value instanceof PlainDate || value instanceof Instant)
    return value.toString();
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return JSON.stringify(value);
}

/**
 * A command's values are text in the definition, so they are converted here the way the wire layer
 * converts a body. Refusing to read "0" into a decimal would make half the language unusable from
 * a command.
 */
function coerce(type: string, value: string | null): unknown {
  if (value === null) return null;

  switch (type) {
    case "integer": {
      const parsed = Number(value);
      return Number.isInteger(parsed) ? parsed : null;
    }
    case "decimal":
    case "money":
      return Dec.parse(value);
    case "boolean":
      return value === "true" || value === "1" || value === "yes";
    case "date":
      return PlainDate.parse(value);
    case "datetime":
      return Instant.parse(value);
    case "multiselect":
      return value === "" ? [] : value.split(",").map((entry) => entry.trim());
    case "json":
      try {
        return JSON.parse(value);
      } catch {
        return value;
      }
    default:
      return value;
  }
}
