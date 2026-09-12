// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router, type Request, type Response } from "express";
import type { CordangoRuntime } from "../app/runtime.js";
import { administratorRole, describeUser, type AppUser } from "../identity/users.js";
import { asObject, refuse, text } from "./account-router.js";
import { state } from "./context.js";
import { handler } from "./error-handler.js";
import { requireAdministrator } from "./guards.js";

/**
 * Managing the accounts: who exists, what they may do, and who is shut out.
 *
 * Administrator-only, enforced here rather than by a grant in the definition. The administrator is
 * the runtime's own bypass, and an application that could grant "manage the accounts" from its own
 * definition would be an application whose author can promote themselves by editing a file.
 */
export function adminRouter(runtime: CordangoRuntime): Router {
  const router = Router();
  router.use(requireAdministrator());

  /**
   * The roles a definition declared, which is what may be ASSIGNED.
   *
   * Not the roles the tables happen to hold. The two differ after a definition changes, and the
   * definition is the one that means anything: a role in the tables and not the definition grants
   * exactly nothing, and offering it in a picker is an invitation to spend an afternoon on it.
   */
  router.get(
    "/roles",
    handler((_request: Request, response: Response) => {
      response.json({
        administrator: administratorRole,
        roles: runtime.permissions.roles.map((role) => ({
          key: role.key,
          entities: role.grants.map((grant) => grant.entity),
        })),
      });
    }),
  );

  /** Every account, with its roles and the person it belongs to. */
  router.get(
    "/users",
    handler(async (_request: Request, response: Response) => {
      response.json((await runtime.users.list()).map(describeUser));
    }),
  );

  /**
   * Create an account, with a password the administrator chooses and the person must replace.
   *
   * **Why the administrator sets it.** The alternatives both cost something a self-hosted
   * application does not have. An emailed invitation needs a mail server, and an application that
   * cannot onboard anybody until SMTP is configured has made its own first step conditional on
   * infrastructure nobody has yet — and when it is misconfigured it fails silently, which is worse
   * than not offering it. A one-time link needs no mail server but does need a token table and an
   * expiry policy.
   *
   * So: a password, handed over however two colleagues already talk to each other, and
   * `mustChangePassword` set so that it stops being a password two people know the first time it is
   * used.
   */
  router.post(
    "/users",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const email = text(body["email"]).trim();
      const password = text(body["password"]);

      if (email === "" || password === "")
        throw refuse(request, 400, "auth.credentials_required", "Enter an email address and a password.");

      const roles = roleList(body["roles"]);
      const unassignable = refused(runtime, roles);
      if (unassignable !== null)
        throw refuse(request, 400, "user.role_unknown", unassignable);

      const created = await runtime.users.create({
        email,
        password,
        displayName: text(body["displayName"]),
        roles,
        mustChangePassword: true,
      });

      // Attached to the person the administrator picked, or to the one already in the directory
      // with this address, or to a new one. Never left unattached: a login with no person record
      // finds every "assigned to me" screen empty and nothing on the page says why.
      await runtime.linkPerson(created.id, created.email, text(body["personId"]));

      const linked = (await runtime.users.findById(created.id)) ?? created;
      response.status(201).json(describeUser(linked));
    }),
  );

  /** Rename an account, move it to a different person, or change what it may do. */
  router.put(
    "/users/:id",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const user = await find(runtime, request);

      if (body["roles"] !== undefined) {
        const roles = roleList(body["roles"]);

        const unassignable = refused(runtime, roles);
        if (unassignable !== null) throw refuse(request, 400, "user.role_unknown", unassignable);

        // Taking your own administrator role away is a one-way door: the endpoint that would give
        // it back is this one, and you would no longer be allowed to call it.
        if (
          isSelf(request, user)
          && user.roles.includes(administratorRole)
          && !roles.includes(administratorRole)
        )
          throw refuse(
            request,
            400,
            "user.self_demote",
            "You cannot remove your own administrator role. Ask another administrator to do it.",
          );

        await refuseIfLastAdministrator(runtime, request, user, roles);
        await runtime.users.setRoles(user.id, roles);
      }

      await runtime.users.updateProfile(user.id, {
        ...(body["displayName"] === undefined ? {} : { displayName: text(body["displayName"]) }),
        ...(body["personId"] === undefined ? {} : { personId: text(body["personId"]) }),
      });

      const updated = await runtime.users.findById(user.id);
      response.json(describeUser(updated ?? user));
    }),
  );

  /**
   * Set somebody's password for them, because they cannot get in to do it themselves.
   *
   * The security stamp moves with it, so every session and access key the old password backed stops
   * working. A reset that leaves the old sessions alive is not a reset — it is a second password.
   */
  router.post(
    "/users/:id/password",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const password = text(body["password"]);
      if (password === "") throw refuse(request, 400, "auth.password_required", "Enter a new password.");

      const user = await find(runtime, request);
      await runtime.users.setPassword(user.id, password, true);
      response.status(204).end();
    }),
  );

  /**
   * Lock an account out, or let it back in.
   *
   * A lockout rather than a deletion, because deleting somebody erases the account that created and
   * approved things.
   */
  router.post(
    "/users/:id/lock",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const locked = body["locked"] === true;
      const user = await find(runtime, request);

      if (locked && isSelf(request, user))
        throw refuse(request, 400, "user.self_lock", "You cannot lock yourself out.");

      if (locked) await refuseIfLastAdministrator(runtime, request, user, []);

      await runtime.users.setLocked(user.id, locked);
      response.status(204).end();
    }),
  );

  /**
   * Delete an account.
   *
   * The LOGIN, not the person. The directory record stays, along with everything it created and
   * approved — a leaver who takes their signatures with them is a hole in the history, not a
   * tidy-up.
   */
  router.delete(
    "/users/:id",
    handler(async (request: Request, response: Response) => {
      const user = await find(runtime, request);

      if (isSelf(request, user))
        throw refuse(request, 400, "user.self_delete", "You cannot delete your own account.");

      await refuseIfLastAdministrator(runtime, request, user, []);

      // A key outliving its owner is a credential with nobody to revoke it.
      await runtime.accessKeys.revokeAllFor(user.id);

      // The person keeps existing and simply stops having a login.
      await runtime.unlinkPerson(user.id);

      await runtime.users.delete(user.id);
      response.status(204).end();
    }),
  );

  return router;
}

async function find(runtime: CordangoRuntime, request: Request): Promise<AppUser> {
  const user = await runtime.users.findById(String(request.params["id"]));
  if (user === null) throw refuse(request, 404, "user.not_found", "No account has that id.");
  return user;
}

function isSelf(request: Request, user: AppUser): boolean {
  return state(request).user.userId === user.id;
}

function roleList(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((entry): entry is string => typeof entry === "string" && entry !== "");
}

/**
 * A role nobody declared, named in a refusal.
 *
 * The check is against the DEFINITION plus the administrator, and never against the tables. A role
 * that exists only because somebody assigned it once grants nothing, and letting it be assigned
 * again would make an account look permitted while permitting nothing.
 */
function refused(runtime: CordangoRuntime, roles: readonly string[]): string | null {
  const declared = new Set(runtime.permissions.roles.map((role) => role.key));
  declared.add(administratorRole);

  const unknown = roles.filter((role) => !declared.has(role));
  if (unknown.length === 0) return null;

  return `This application declares no role called ${unknown.map((role) => `'${role}'`).join(", ")}.`;
}

/**
 * Would this change leave the application with nobody who can administer it?
 *
 * Asked before every demotion, lockout and deletion. An application with no administrator cannot be
 * given one back through any endpoint it serves — the only route left is editing the database by
 * hand, which is exactly the situation a generated application should never talk somebody into.
 */
async function refuseIfLastAdministrator(
  runtime: CordangoRuntime,
  request: Request,
  user: AppUser,
  rolesAfter: readonly string[],
): Promise<void> {
  if (!user.roles.includes(administratorRole)) return;
  if (rolesAfter.includes(administratorRole)) return;

  if ((await runtime.users.administratorCount()) <= 1)
    throw refuse(
      request,
      400,
      "user.last_administrator",
      "This is the only administrator. Give somebody else the role first.",
    );
}
