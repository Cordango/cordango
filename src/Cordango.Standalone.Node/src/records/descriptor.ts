// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root.

/**
 * The Cord field types as the runtime handles them; the generator maps its
 * fields onto these.
 */
export type FieldType =
  | "text" | "integer" | "decimal" | "money" | "boolean" | "date"
  | "datetime" | "reference" | "id" | "multiselect" | "json";

export interface RecordField {
  readonly key: string;
  readonly type: FieldType;
}

/**
 * The id plus whatever fields the entity carries, keyed by their wire names.
 */
export interface RecordRow {
  id: string;
  [key: string]: unknown;
}

export class RecordDescriptor<T extends RecordRow> {
  private readonly byKey: Map<string, RecordField>;

  constructor(
    readonly entityKey: string,
    readonly label: string,
    readonly fields: readonly RecordField[],
  ) {
    this.byKey = new Map(fields.map((field) => [field.key, field]));
  }

  tryGetField(key: string): RecordField | undefined {
    return this.byKey.get(key);
  }

  get fieldKeys(): string[] {
    return this.fields.map((field) => field.key);
  }

  /**
   * A detached copy carrying id and every field — update hooks are handed the
   * row as it was.
   */
  copy(source: T): T {
    const copy = { id: source.id } as T;
    for (const field of this.fields) copy[field.key] = source[field.key];
    return copy;
  }

  /**
   * Keys the entity does not have are ignored here — rejecting them is the
   * caller's decision.
   */
  apply(incoming: T, target: T, fieldKeys: Iterable<string>): void {
    for (const key of fieldKeys) {
      const field = this.byKey.get(key);
      if (field) target[field.key] = incoming[field.key];
    }
  }
}
