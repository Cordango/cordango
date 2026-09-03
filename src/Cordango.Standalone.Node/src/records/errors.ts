// SPDX-License-Identifier: Apache-2.0
// Copyright (c) The Cordango Authors. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root.

/**
 * The error body, and the only shape this application returns for one.
 * `code` is stable and machine-readable; `error` is already in the reader's
 * language.
 */
export interface ApiError {
  code: string;
  error: string;
  fields?: readonly string[];
}

/**
 * A refusal with an answer already in it.
 */
export class RecordError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly statusCode = 400,
    readonly fields?: readonly string[],
  ) {
    super(message);
    this.name = "RecordError";
  }

  toApiError(translated?: string): ApiError {
    const error: ApiError = { code: this.code, error: translated ?? this.message };
    if (this.fields) error.fields = this.fields;
    return error;
  }

  static notFound(entityKey: string, id: string): RecordError {
    return new RecordError("record.not_found", `No ${entityKey} with id '${id}'.`, 404);
  }

  static forbidden(code: string, message: string): RecordError {
    return new RecordError(code, message, 403);
  }

  /**
   * Named all at once, because discovering them one round trip at a time is a
   * worse experience.
   */
  static writeRestricted(fields: readonly string[]): RecordError {
    return new RecordError(
      "record.field_write_denied",
      `Your role may not set: ${fields.join(", ")}.`,
      403,
      fields,
    );
  }
}
