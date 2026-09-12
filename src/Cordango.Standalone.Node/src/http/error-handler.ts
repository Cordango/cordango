// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { ErrorRequestHandler, NextFunction, Request, RequestHandler, Response } from "express";
import { RecordError, type ApiError } from "../records/errors.js";
import { passThroughMessages, type ApiMessages } from "./messages.js";

/** Where an unhandled failure is written. The default is the console; a generated application can
 * hand in whatever it logs through. */
export interface ErrorLog {
  warn(message: string, error?: unknown): void;
  error(message: string, error?: unknown): void;
}

export const consoleLog: ErrorLog = {
  warn: (message, error) => console.warn(message, error ?? ""),
  error: (message, error) => console.error(message, error ?? ""),
};

/**
 * Turns anything thrown below it into the {@link ApiError} wire, and turns anything else into a
 * 500 that says nothing.
 *
 * **Two rules, and the second one is the point.** A {@link RecordError} is a decision the
 * application made, so its code and message go to the caller as written. Anything else is a bug:
 * it is logged in full and the caller gets `{"code":"server.error"}` and nothing more. Exception
 * text is written for whoever is holding the stack trace, and handing it to an anonymous client
 * is how internal paths, table names and connection strings end up in a bug report somebody else
 * files.
 */
export function errorHandler(log: ErrorLog = consoleLog): ErrorRequestHandler {
  return (error: unknown, request: Request, response: Response, next: NextFunction): void => {
    // A handler that has already started writing cannot be given a different status, and trying
    // throws a second error on top of the first. Let the original one surface in the log.
    if (response.headersSent) {
      log.error(`Unhandled failure after the response had started for ${request.method} ${request.path}.`, error);
      next(error);
      return;
    }

    const messages = request.cordango?.messages ?? passThroughMessages;
    const language = request.headers["accept-language"];

    if (error instanceof RecordError) {
      write(response, error.statusCode, error.toApiError(messages.translate(error.code, error.message, language)));
      return;
    }

    // Express answers a malformed JSON body by throwing a SyntaxError with a status on it. Its own
    // case, because a bad payload is the caller's news and a server error is not.
    if (isBodyParseFailure(error)) {
      write(response, 400, refusal(messages, language, "request.body_invalid", "The request body was not valid JSON."));
      return;
    }

    log.error(`Unhandled failure for ${request.method} ${request.path}.`, error);
    write(
      response,
      500,
      refusal(messages, language, "server.error", "Something went wrong. The failure has been logged."),
    );
  };
}

function isBodyParseFailure(error: unknown): boolean {
  return (
    typeof error === "object"
    && error !== null
    && (error as { type?: unknown }).type === "entity.parse.failed"
  );
}

function refusal(
  messages: ApiMessages,
  language: string | undefined,
  code: string,
  fallback: string,
): ApiError {
  return { code, error: messages.translate(code, fallback, language) };
}

function write(response: Response, status: number, body: ApiError): void {
  response.status(status).type("application/json; charset=utf-8").send(JSON.stringify(body));
}

/**
 * A route that is allowed to throw.
 *
 * Express 5 forwards a rejected promise to the error handler on its own, so this is not the
 * wrapper it had to be on Express 4. It stays as the one place a handler's return type is pinned
 * to `void`, which is what stops a handler that forgot to `await` from resolving before its work
 * is done and answering with an empty body.
 */
export function handler(
  route: (request: Request, response: Response) => Promise<void> | void,
): RequestHandler {
  return (request, response, next) => {
    try {
      const result = route(request, response);
      if (result instanceof Promise) result.catch(next);
    } catch (error) {
      next(error);
    }
  };
}

/** The 404 for an API route nothing answered. Without it an unknown `/api/...` path falls through
 * to the single-page app and the client gets HTML where it expected `{code, error}`. */
export function apiNotFound(): RequestHandler {
  return (request, _response, next) => {
    next(
      new RecordError(
        "route.not_found",
        `No API route matches ${request.method} ${request.path}.`,
        404,
      ),
    );
  };
}
