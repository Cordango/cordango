// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import express, { Router, type Express, type Request } from "express";
import { join } from "node:path";
import { accountRouter } from "../http/account-router.js";
import { adminRouter } from "../http/admin-router.js";
import { antiforgery, type AntiforgeryOptions } from "../http/antiforgery.js";
import { apiNotFound, consoleLog, errorHandler, handler, type ErrorLog } from "../http/error-handler.js";
import { mediaRouter } from "../http/media-router.js";
import { notificationsRouter } from "../http/notifications-router.js";
import { settingsRouter } from "../http/settings-router.js";
import { recordsRouter } from "../http/records-router.js";
import { directoryRouter } from "../directory/module.js";
import type { FileStore } from "../media/file-store.js";
import { readTicket } from "../identity/sessions.js";
import type { CordangoRuntime } from "./runtime.js";

export interface AppServerOptions {
  readonly runtime: CordangoRuntime;
  /** Where uploaded files live. Without one, the media routes are not mounted at all — an
   * application with no attachment field has nothing to upload. */
  readonly files?: FileStore;
  /**
   * The built single-page app. Anything that is not an API route is served from here, falling back
   * to `index.html` so the client's own router decides what a path means.
   *
   * Absent during `npm run dev`, where Vite serves the front end on its own port and proxies `/api`
   * here.
   */
  readonly staticRoot?: string | null;
  readonly log?: ErrorLog;
  /** Mount this application's own entity routes, and anything else it serves under `/api`. */
  readonly routes?: (api: Router, runtime: CordangoRuntime) => void;
  /** Largest JSON body accepted. A record with a long text field is the reason this is not the
   * library default of 100 kB. */
  readonly bodyLimit?: string;
}

/**
 * The whole application as an Express app, in the order the middleware has to run.
 *
 * **The order is the security model**, so it is here rather than in each generated application
 * where it could be got subtly wrong once per app:
 *
 * 1. the body is parsed, so a route has something to read;
 * 2. the built bundle is served — above the session, because none of it is private and a cold load
 *    is a dozen requests that would each otherwise cost a database read;
 * 3. the caller is identified, so everything after it knows who is asking;
 * 4. the antiforgery check runs — AFTER identification, because the token is bound to the session
 *    and a check that ran first would be checking against nobody;
 * 5. the API routes answer;
 * 6. anything under `/api` that nothing answered is a 404 in the `{code, error}` shape, never the
 *    single-page app's HTML;
 * 7. everything else is a page of the single-page app;
 * 8. the error boundary turns whatever was thrown into that same shape.
 */
export function createApp(options: AppServerOptions): Express {
  const { runtime } = options;
  const app = express();

  // Express advertises itself in a header on every response. It tells an attacker which stack to
  // look up known issues for and tells a legitimate caller nothing at all.
  app.disable("x-powered-by");

  // Behind a reverse proxy that terminates TLS, the application sees plain HTTP unless the proxy's
  // X-Forwarded-Proto is honoured — and then every "is this request secure" decision is wrong.
  if (process.env["SECURITY_BEHIND_PROXY"] === "true") app.set("trust proxy", true);

  app.use(express.json({ limit: options.bodyLimit ?? "2mb" }));

  // The built bundle, BEFORE anything works out who is asking.
  //
  // Identifying a caller costs a database read, and a cold load of the front end is a dozen
  // requests for JavaScript, CSS and fonts — none of which is private, and none of which would do
  // anything differently for one person than another. Serving them above the session middleware
  // takes that read off every one of them.
  //
  // It does not skip the antiforgery token: the single-page app asks `/api/account/me` before it
  // renders anything, and that request comes back with the cookie. `index.html` is deliberately NOT
  // served here — it falls through to the catch-all below, so a deep link is still a page rather
  // than a 404.
  if (options.staticRoot != null)
    app.use(express.static(options.staticRoot, { index: false }));

  // Who is asking, and everything this request may reach. Before any route, because every route
  // below reads the scope it hangs off the request.
  app.use(runtime.middleware());

  const antiforgeryOptions: AntiforgeryOptions = {
    secret: runtime.sessionOptions.secret,
    secure: runtime.secure,
  };

  app.use(
    antiforgery(
      antiforgeryOptions,
      (request: Request) => readTicket(request, runtime.sessionOptions)?.userId ?? null,
      // A bearer token is not a cookie. It is never attached by the browser to a request another
      // site provoked, so there is nothing to forge and nothing for a token to protect against —
      // and a script or an MCP client has no way to be asked for one.
      (request: Request) => (request.headers.authorization ?? "").toLowerCase().startsWith("bearer "),
    ),
  );

  const api = Router();

  // Liveness, before anything that needs a session. A container orchestrator has no credential and
  // should not need one to find out whether the process is up.
  api.get(
    "/health",
    handler((_request, response) => {
      response.json({ status: "ok", app: runtime.appKey });
    }),
  );

  api.use("/account", accountRouter(runtime));
  api.use("/admin", adminRouter(runtime));
  api.use("/directory", directoryRouter());
  api.use("/notifications", notificationsRouter(runtime));
  api.use("/settings", settingsRouter(runtime));
  if (options.files !== undefined) api.use("/media", mediaRouter(options.files));

  // This application's own entities, mounted by the generated setup.
  if (options.routes !== undefined) options.routes(api, runtime);

  // Anything under /api that nothing answered. Without it a mistyped endpoint falls through to the
  // single-page app and the client gets HTML where it expected `{code, error}`.
  api.use(apiNotFound());

  app.use("/api", api);

  // Anything that is not an API route and not a file on disk is a page of the single-page app, and
  // the app's router decides what it means.
  if (options.staticRoot != null)
    app.get(/.*/, (_request, response) => {
      response.sendFile(join(options.staticRoot as string, "index.html"));
    });

  app.use(errorHandler(options.log ?? consoleLog));

  return app;
}

/**
 * Mount one route per entity the runtime knows about.
 *
 * Offered for an application that would rather not list them — the generated setup lists them
 * explicitly, because a route table you can read is worth more than a line that saves typing.
 */
export function mountEntities(api: Router, runtime: CordangoRuntime): void {
  for (const entity of runtime.entities) api.use(`/${entity}`, recordsRouter(entity));
}
