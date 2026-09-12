// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router, type Request, type Response } from "express";
import type { CordangoRuntime } from "../app/runtime.js";
import { administratorRole, describeUser, isAdministrator, type AppUser } from "../identity/users.js";
import { verifyPassword } from "../identity/passwords.js";
import { signIn, signOut } from "../identity/sessions.js";
import { RecordError } from "../records/errors.js";
import { rotate, type AntiforgeryOptions } from "./antiforgery.js";
import { state, translate } from "./context.js";
import { handler } from "./error-handler.js";
import { requireSignIn } from "./guards.js";

/**
 * Signing in, signing out, asking who you are — and, exactly once in the life of a database,
 * creating the account that everything else needs.
 *
 * Every route that changes something is a POST, and every POST in this application requires the
 * antiforgery token — including these. That is not ceremony: the sign-in cookie is attached by the
 * browser to any request any page can provoke, so without a token that only this origin can read,
 * another site could sign somebody into an account of its choosing and watch what they do next.
 */
export function accountRouter(runtime: CordangoRuntime): Router {
  const router = Router();
  const antiforgery: AntiforgeryOptions = { secret: runtime.sessionOptions.secret, secure: runtime.secure };

  /**
   * One setup at a time, per process.
   *
   * Without it, submitting the first-run form twice in quick succession — a double click, or a
   * client that retries — runs two check-then-creates that both see an empty database and both
   * succeed, leaving two administrators where the person asked for one. This does NOT close the
   * same race across two instances of this application starting against one database, which would
   * need a lock in Postgres; that is a real hole and a deliberately unpatched one, because a
   * deployment running several instances is one that should be setting ADMIN_EMAIL and
   * ADMIN_PASSWORD and never seeing this endpoint at all.
   */
  let setupInFlight: Promise<unknown> = Promise.resolve();

  router.post(
    "/login",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const email = text(body["email"]);
      const password = text(body["password"]);

      if (email === "" || password === "")
        throw refuse(request, 400, "auth.credentials_required", "Enter an email address and a password.");

      const result = await runtime.users.checkCredentials(email, password);

      if (result.outcome === "locked")
        throw refuse(request, 423, "auth.locked_out", "Too many attempts. Try again in a few minutes.");

      // One answer for "no such account" and for "wrong password", deliberately. Two different
      // answers turn the login form into a way to find out who has an account here.
      if (result.outcome === "invalid")
        throw refuse(request, 401, "auth.invalid_credentials", "That email address and password do not match.");

      establish(response, runtime, antiforgery, result.user, body["rememberMe"] === true);
      response.json(describeSession(result.user));
    }),
  );

  /**
   * Create the first administrator, from the browser, on a database that has none.
   *
   * **Why this is anonymous, and why that is not a hole.** It answers only while this application
   * has no administrator whatsoever, which is true for the seconds between a fresh database coming
   * up and the person who started it opening the page. From the moment an account exists it returns
   * 409 to everyone, forever, and no configuration can reopen it — only deleting every
   * administrator can, which is a deliberate act and is also the recovery path when somebody loses
   * their only account.
   *
   * The window is real, and it is the same one every self-hosted application has. If the first
   * request to reach this application will not be yours, set ADMIN_EMAIL and ADMIN_PASSWORD, and
   * the account exists before the first packet arrives.
   */
  router.post(
    "/setup",
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const email = text(body["email"]).trim();
      const password = text(body["password"]);

      if (email === "" || password === "")
        throw refuse(request, 400, "auth.credentials_required", "Enter an email address and a password.");

      // Serialised against itself, so a double-submitted form creates one administrator.
      const run = setupInFlight.then(async () => {
        if (await runtime.users.administratorExists())
          throw refuse(
            request,
            409,
            "setup.completed",
            "This application already has an administrator. Sign in instead.",
          );

        return runtime.users.create({
          email,
          password,
          displayName: text(body["displayName"]).trim(),
          roles: [administratorRole],
        });
      });

      // The gate must reopen whether the attempt succeeded or failed, or one rejected password
      // would deadlock every later attempt.
      setupInFlight = run.catch(() => undefined);
      const created = await run;

      // A login and a directory Person are separate things, and this account needs both: the
      // definition's own screens filter on the PERSON, so an administrator with no person record
      // signs in perfectly and finds every one of those screens empty.
      await runtime.linkPerson(created.id, created.email);

      // Signed in on the spot. Making somebody type the password they chose four seconds ago into a
      // login form proves nothing and reads as a bug. Re-read first, because linkPerson wrote the
      // person id onto the account and the session should describe the account as it now is.
      const linked = (await runtime.users.findById(created.id)) ?? created;
      establish(response, runtime, antiforgery, linked, false);
      response.json(describeSession(linked));
    }),
  );

  router.post(
    "/logout",
    handler((_request: Request, response: Response) => {
      signOut(response, runtime.sessionOptions);
      rotate(response, antiforgery, null);
      response.status(204).end();
    }),
  );

  /**
   * Who the caller is. The one call the single-page app makes before rendering anything, so that a
   * reload lands on the page a signed-in person expects rather than on the login form — and, when
   * nobody is signed in, whether this database has been set up at all.
   */
  router.get(
    "/me",
    handler(async (request: Request, response: Response) => {
      const account = state(request).account;

      // `setupRequired` is only ever computed for a caller who is not signed in. Somebody holding a
      // session is proof enough that an account exists, and this way the query does not run on the
      // request every page of the application makes.
      if (account === null) {
        response.json({
          authenticated: false,
          setupRequired: !(await runtime.users.administratorExists()),
        });
        return;
      }

      response.json({
        authenticated: true,
        setupRequired: false,
        id: account.id,
        email: account.email,
        displayName: account.displayName,
        personId: account.personId,
        roles: account.roles,
        // The client holds them on the change-password screen while this is true. The SERVER does
        // not refuse anything on it: an account that cannot read its own record cannot be told why
        // it is being asked to change a password, and a person locked out of a form they are
        // required to complete has no way forward at all.
        mustChangePassword: account.mustChangePassword,
        isAdministrator: account.isAdministrator,
      });
    }),
  );

  router.post(
    "/password",
    requireSignIn(),
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const current = text(body["currentPassword"]);
      const replacement = text(body["newPassword"]);

      if (current === "" || replacement === "")
        throw refuse(request, 400, "auth.password_required", "Enter your current password and a new one.");

      const account = state(request).account;
      if (account === null) throw refuse(request, 401, "auth.required", "Sign in first.");

      const user = await runtime.users.findById(account.id);
      if (user === null) throw refuse(request, 401, "auth.required", "Sign in first.");

      if (!(await verifyPassword(current, user.passwordHash)))
        throw refuse(request, 400, "auth.password_rejected", "Your current password is not correct.");

      // Clears mustChangePassword: whatever they were handed, they have now replaced.
      await runtime.users.setPassword(user.id, replacement, false);

      // The old cookie was minted against the old security stamp, which setPassword has just moved.
      // Re-issuing here keeps the person signed in on THIS device instead of bouncing them to the
      // login form they just proved themselves at — while every other device they were signed in on
      // is signed out, which is what a password change is for.
      const refreshed = await runtime.users.findById(user.id);
      if (refreshed !== null) establish(response, runtime, antiforgery, refreshed, false);

      response.status(204).end();
    }),
  );

  /**
   * This person's access keys.
   *
   * Never anybody else's, and never a secret: what comes back is the label, the dates and the id —
   * enough to decide which one to revoke and nothing that could be used to sign in.
   */
  router.get(
    "/keys",
    requireSignIn(),
    handler(async (request: Request, response: Response) => {
      const userId = state(request).user.userId as string;
      const keys = await runtime.accessKeys.list(userId);

      response.json(
        keys.map((key) => ({
          id: key.id,
          label: key.label,
          created: key.created.toISOString(),
          lastUsed: key.lastUsed === null ? null : key.lastUsed.toISOString(),
          expires: key.expires === null ? null : key.expires.toISOString(),
        })),
      );
    }),
  );

  /**
   * Mint one, for a script, a CI job or an AI client.
   *
   * **The token comes back once and is never recoverable.** Only its hash is stored, so there is no
   * second chance to copy it and no way for anyone reading the database to use it. A key acts as
   * its owner: it reaches exactly what this person reaches through the screens, and it stops
   * working the moment their account is locked.
   */
  router.post(
    "/keys",
    requireSignIn(),
    handler(async (request: Request, response: Response) => {
      const body = asObject(request.body);
      const userId = state(request).user.userId as string;

      const expires = body["expires"] === undefined || body["expires"] === null
        ? null
        : new Date(text(body["expires"]));

      if (expires !== null && Number.isNaN(expires.getTime()))
        throw refuse(request, 400, "key.expiry_invalid", "That expiry date could not be read.");

      const minted = await runtime.accessKeys.mint(userId, text(body["label"]), expires);

      response.json({
        id: minted.key.id,
        label: minted.key.label,
        created: minted.key.created.toISOString(),
        expires: minted.key.expires === null ? null : minted.key.expires.toISOString(),
        token: minted.token,
        notice: "Copy this now. It is not stored and cannot be shown again.",
      });
    }),
  );

  router.delete(
    "/keys/:id",
    requireSignIn(),
    handler(async (request: Request, response: Response) => {
      const userId = state(request).user.userId as string;

      // The same answer whether the key was somebody else's or never existed. Either way it is not
      // yours, and saying which would let somebody test ids against other people's keys.
      if (!(await runtime.accessKeys.revoke(userId, String(request.params["id"]))))
        throw refuse(request, 404, "key.not_found", "No access key of yours has that id.");

      response.status(204).end();
    }),
  );

  return router;
}

/** Put a session on the response, and mint an antiforgery token bound to it. Both, always: a
 * session whose token is still bound to the previous caller fails the first write after sign-in. */
function establish(
  response: Response,
  runtime: CordangoRuntime,
  antiforgery: AntiforgeryOptions,
  user: AppUser,
  persistent: boolean,
): void {
  signIn(response, runtime.sessionOptions, {
    userId: user.id,
    stamp: user.securityStamp,
    persistent,
  });
  rotate(response, antiforgery, user.id);
}

/** What `/login` and `/setup` answer with — the same body `/me` returns, so the client applies one
 * shape however the session began. */
function describeSession(user: AppUser): Record<string, unknown> {
  const summary = describeUser(user);
  return {
    authenticated: true,
    setupRequired: false,
    id: summary.id,
    email: summary.email,
    displayName: summary.displayName,
    personId: summary.personId,
    roles: summary.roles,
    mustChangePassword: summary.mustChangePassword,
    isAdministrator: isAdministrator(user),
  };
}

export function asObject(body: unknown): Record<string, unknown> {
  return typeof body === "object" && body !== null && !Array.isArray(body)
    ? (body as Record<string, unknown>)
    : {};
}

export function text(value: unknown): string {
  return typeof value === "string" ? value : "";
}

/** A refusal in the caller's language, built where the route decides rather than thrown blind. The
 * code travels; the sentence is chosen here. */
export function refuse(
  request: Request,
  status: number,
  code: string,
  fallback: string,
  fields?: readonly string[],
): RecordError {
  return new RecordError(code, translate(request, code, fallback), status, fields);
}
