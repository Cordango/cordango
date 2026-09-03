// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root.

import type { Instant } from "../calc/dates.js";
import type { CurrentUser } from "../security/permissions.js";

/**
 * Where "now" comes from — an interface so a test can hold the clock still.
 */
export interface Clock {
  readonly utcNow: Instant;
}

/**
 * Deliberately not the HTTP context, because hooks run for seeds and workflows
 * too.
 */
export interface RecordContext {
  readonly user: CurrentUser;
  readonly clock: Clock;
}

export type BeforeCreateHook<T> = (record: T, context: RecordContext) => Promise<void> | void;
export type AfterCreateHook<T> = (record: T, context: RecordContext) => Promise<void> | void;
export type BeforeUpdateHook<T> = (record: T, before: T, context: RecordContext) => Promise<void> | void;
export type AfterUpdateHook<T> = (record: T, before: T, context: RecordContext) => Promise<void> | void;
export type BeforeDeleteHook<T> = (record: T, context: RecordContext) => Promise<void> | void;
export type AfterDeleteHook<T> = (record: T, context: RecordContext) => Promise<void> | void;

/**
 * Every hook is awaited before the next step begins.
 */
export class RecordHooks<T> {
  static readonly none = new RecordHooks<never>([], [], [], [], [], []);

  constructor(
    private readonly beforeCreateHooks: readonly BeforeCreateHook<T>[],
    private readonly afterCreateHooks: readonly AfterCreateHook<T>[],
    private readonly beforeUpdateHooks: readonly BeforeUpdateHook<T>[],
    private readonly afterUpdateHooks: readonly AfterUpdateHook<T>[],
    private readonly beforeDeleteHooks: readonly BeforeDeleteHook<T>[],
    private readonly afterDeleteHooks: readonly AfterDeleteHook<T>[],
  ) {}

  async beforeCreate(record: T, context: RecordContext): Promise<void> {
    for (const hook of this.beforeCreateHooks) await hook(record, context);
  }
  async afterCreate(record: T, context: RecordContext): Promise<void> {
    for (const hook of this.afterCreateHooks) await hook(record, context);
  }
  async beforeUpdate(record: T, before: T, context: RecordContext): Promise<void> {
    for (const hook of this.beforeUpdateHooks) await hook(record, before, context);
  }
  async afterUpdate(record: T, before: T, context: RecordContext): Promise<void> {
    for (const hook of this.afterUpdateHooks) await hook(record, before, context);
  }
  async beforeDelete(record: T, context: RecordContext): Promise<void> {
    for (const hook of this.beforeDeleteHooks) await hook(record, context);
  }
  async afterDelete(record: T, context: RecordContext): Promise<void> {
    for (const hook of this.afterDeleteHooks) await hook(record, context);
  }
}
