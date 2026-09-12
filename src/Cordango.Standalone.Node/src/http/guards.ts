// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { RequestHandler } from "express";
import { RecordError } from "../records/errors.js";
import { state } from "./context.js";

/**
 * Somebody has to be signed in.
 *
 * 401 rather than 403: the two mean different things to a client. 401 is "prove who you are", which
 * the single-page app turns into a login prompt; 403 is "you are known and the answer is still no",
 * which it turns into a message. A route that answered 403 to an anonymous caller would leave a
 * signed-out person staring at a permissions error they cannot act on.
 */
export function requireSignIn(): RequestHandler {
  return (request, _response, next) => {
    if (state(request).user.userId === null) {
      next(new RecordError("auth.required", "Sign in first.", 401));
      return;
    }
    next();
  };
}

/**
 * And they have to be the administrator.
 *
 * The administrator is the RUNTIME's own bypass, not one of the definition's roles. Nothing in any
 * definition can name it, and nothing in a definition can grant it — which is what keeps "who may
 * manage the accounts" from being answerable by editing the application.
 */
export function requireAdministrator(): RequestHandler {
  return (request, _response, next) => {
    const user = state(request).user;
    if (user.userId === null) {
      next(new RecordError("auth.required", "Sign in first.", 401));
      return;
    }
    if (!user.isAdministrator) {
      next(RecordError.forbidden("auth.forbidden", "Only an administrator may do that."));
      return;
    }
    next();
  };
}
