// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router } from "express";
import type { CordangoRuntime } from "../app/runtime.js";
import { recordsRouter } from "../http/records-router.js";
import { EntityAccess } from "../security/entity-access.js";
import type { CurrentUser } from "../security/permissions.js";
import { directoryDescriptors, directoryEntities } from "./entities.js";

/**
 * The directory's own access rule: **anyone signed in may read it, only an administrator may change
 * it.**
 *
 * **Why it does not go through the definition's roles.** The definition never mentions these
 * entities — an application says `targetApp: "platform"` and points at a directory it assumes
 * exists. Running them through the permission resolver would therefore answer "nothing" for every
 * role, and every reference picker in the application would come back empty with a 403 nobody could
 * trace to a rule they wrote.
 *
 * The rule chosen instead is the one an organisation chart already implies. Knowing who works here
 * and which team they are on is what makes an approval field usable, and it is not a secret from
 * the people who work here. Editing that chart is administration.
 */
export function directoryAccess(user: CurrentUser): EntityAccess {
  if (user.isAdministrator) return EntityAccess.full;
  return user.userId === null ? EntityAccess.none : EntityAccess.readOnly;
}

/**
 * Register the five directory entities on a runtime.
 *
 * Called by every generated application, before its own entities. They are ordinary registrations
 * with one difference — an access rule of their own — so everything else about them (the store, the
 * query layer, the tracking columns, the tool surface) is exactly what an application's own entity
 * gets.
 */
export function addDirectory(runtime: CordangoRuntime): CordangoRuntime {
  for (const entity of directoryEntities)
    runtime.register({
      descriptor: directoryDescriptors[entity],
      access: directoryAccess,
    });

  return runtime;
}

/**
 * The HTTP face of the directory: `/api/directory/person`, and the four beside it.
 *
 * The same router every other entity gets. An application that needs something narrower — a
 * directory only HR may read — changes the registration's access rule, which applies to every
 * caller rather than only to browsers.
 */
export function directoryRouter(): Router {
  const router = Router();
  for (const entity of directoryEntities) router.use(`/${entity}`, recordsRouter(entity));
  return router;
}
