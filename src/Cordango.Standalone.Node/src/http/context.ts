// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { Request } from "express";
import type { CurrentUser } from "../security/permissions.js";
import type { ApiMessages } from "./messages.js";
import type { RecordRow } from "../records/descriptor.js";
import type { RecordGateway } from "../records/gateway.js";
import type { RecordStoreApi } from "../records/store.js";
import type { Clock } from "../records/hooks.js";

/**
 * Everything one request is allowed to reach, resolved once when it arrives.
 *
 * The equivalent of the scoped container a request gets on the .NET target: one user, one clock,
 * and stores and gateways bound to both. A hook that runs halfway through a write reads the same
 * `now` as the stamp on the row, and a rollup that walks into a sibling entity reaches it as the
 * same caller — neither of which is true if each call site resolves its own.
 */
export interface RequestScope {
  readonly user: CurrentUser;
  readonly clock: Clock;

  /** The raw store for one entity, permissions NOT applied. For hooks, workflows and seeds, which
   * run as the application rather than as the caller. */
  store<T extends RecordRow>(entity: string): RecordStoreApi<T>;

  /** The permission-applying façade over one entity. Everything a request serves goes through
   * this, so an MCP client and a browser reach exactly the same rows. */
  gateway<T extends RecordRow>(entity: string): RecordGateway<T>;

  /** Every entity key this application registered, in definition order. */
  readonly entities: readonly string[];
}

/** What the runtime attaches to each request. */
export interface RequestState {
  readonly scope: RequestScope;
  readonly messages: ApiMessages;
  /** Who the request authenticated as. Anonymous is a real answer, not a missing one. */
  user: CurrentUser;
  /** The signed-in account, when there is one. Null for an anonymous caller and for one holding
   * an access key rather than a session. */
  account: AuthenticatedAccount | null;
}

/** The account behind a session, as the identity store knows it. */
export interface AuthenticatedAccount {
  readonly id: string;
  readonly email: string;
  readonly displayName: string;
  readonly personId: string | null;
  readonly roles: readonly string[];
  readonly isAdministrator: boolean;
  readonly mustChangePassword: boolean;
}

declare global {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace Express {
    interface Request {
      /** Present from the runtime's first middleware onward. Reading it before that is a wiring
       * mistake, and `state()` says so rather than handing back a half-built request. */
      cordango?: RequestState;
    }
  }
}

/** The request's state, or a thrown error naming the wiring mistake. A route that reached this
 * without the runtime's middleware would otherwise fail later with a message about `undefined`. */
export function state(request: Request): RequestState {
  const found = request.cordango;
  if (found === undefined)
    throw new Error(
      "This request never passed through the Cordango runtime middleware. "
        + "Mount it with `app.use(runtime.middleware())` before any route that reads the scope.",
    );
  return found;
}

/** Who is asking. */
export function caller(request: Request): CurrentUser {
  return state(request).user;
}

/** The translator for this request's language. */
export function messagesFor(request: Request): ApiMessages {
  return state(request).messages;
}

/** The message for this code in the caller's language, or the English sentence as written. */
export function translate(request: Request, code: string, fallback: string): string {
  return messagesFor(request).translate(code, fallback, request.headers["accept-language"]);
}
