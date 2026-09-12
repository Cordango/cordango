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
 * One person's saved table layouts.
 *
 * Always their own: the owner comes from the session and never from the URL, so there is no id to
 * tamper with and no way to read somebody else's. That is also why there is no administrator view
 * of this — a column width is not something anybody needs to see on another person's behalf.
 */
export function settingsRouter(runtime: CordangoRuntime): Router {
  const router = Router();
  router.use(requireSignIn());

  router.get(
    "/table/:handle/:tableKey",
    handler(async (request: Request, response: Response) => {
      const userId = state(request).user.userId as string;
      const settings = await runtime.tableSettings.read(
        userId,
        String(request.params["handle"]),
        String(request.params["tableKey"]),
      );

      // `null` rather than a 404. Having no saved layout is the ordinary state of every table the
      // first time anybody opens it, and a 404 would make the client's own "this is fine" path go
      // through its error handling.
      response.json(settings);
    }),
  );

  router.put(
    "/table/:handle/:tableKey",
    handler(async (request: Request, response: Response) => {
      const userId = state(request).user.userId as string;
      await runtime.tableSettings.write(
        userId,
        String(request.params["handle"]),
        String(request.params["tableKey"]),
        request.body,
      );
      response.status(204).end();
    }),
  );

  router.delete(
    "/table/:handle/:tableKey",
    handler(async (request: Request, response: Response) => {
      const userId = state(request).user.userId as string;
      await runtime.tableSettings.clear(
        userId,
        String(request.params["handle"]),
        String(request.params["tableKey"]),
      );
      response.status(204).end();
    }),
  );

  return router;
}
