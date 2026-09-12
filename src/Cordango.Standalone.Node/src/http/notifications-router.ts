// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router, type Request, type Response } from "express";
import type { CordangoRuntime } from "../app/runtime.js";
import { state } from "./context.js";
import { handler } from "./error-handler.js";
import { requireSignIn } from "./guards.js";

/**
 * What is waiting for you.
 *
 * Addressed to a PERSON, not to a login: a `notify` effect names somebody in the directory, and a
 * colleague who has not been given a login yet still has things addressed to them. An account with
 * no person behind it therefore has an empty list rather than an error — which is the honest
 * answer, not a degraded one.
 */
export function notificationsRouter(runtime: CordangoRuntime): Router {
  const router = Router();
  router.use(requireSignIn());

  router.get(
    "/",
    handler(async (request: Request, response: Response) => {
      const person = state(request).account?.personId ?? null;
      if (person === null) {
        response.json({ items: [], unread: 0 });
        return;
      }

      const items = await runtime.notifications.list(person);
      response.json({ items, unread: items.filter((item) => item.read_at === null).length });
    }),
  );

  router.post(
    "/:id/read",
    handler(async (request: Request, response: Response) => {
      const person = state(request).account?.personId ?? null;
      // Not a 404. Whether a notification with that id exists is not something somebody with no
      // person record is entitled to find out, and there is nothing for them to mark read either
      // way.
      if (person !== null) await runtime.notifications.markRead(person, String(request.params["id"]));
      response.status(204).end();
    }),
  );

  router.post(
    "/read-all",
    handler(async (request: Request, response: Response) => {
      const person = state(request).account?.personId ?? null;
      if (person !== null) await runtime.notifications.markAllRead(person);
      response.status(204).end();
    }),
  );

  return router;
}
