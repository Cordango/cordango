// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router, type Request, type Response } from "express";
import type { RecordRow } from "../records/descriptor.js";
import { parseFilters, parseSort } from "../records/query.js";
import { handler } from "./error-handler.js";
import { state } from "./context.js";
import { requireSignIn } from "./guards.js";

/**
 * CRUD over one entity, with the definition's permissions enforced on every path.
 *
 * **How a generated application uses it.** One line per entity, and the line is all there is to it:
 *
 * ```ts
 * app.use("/api/expense", recordsRouter("expense"));
 * ```
 *
 * **What is deliberately NOT here.** Whether a caller may read a field, what a refusal is called,
 * how a partial update differs from a replace: all of that lives in `RecordGateway`, because HTTP
 * is not the only way into this application. An MCP client goes through the same gateway, so it
 * reaches exactly what the same person reaches here — rather than through a second implementation
 * of the same rules that drifts from this one. What is left is the part that really is about HTTP:
 * routes, status codes, and turning a query string into filters.
 */
export function recordsRouter(entity: string): Router {
  const router = Router({ mergeParams: true });

  // Every record route needs somebody. An anonymous caller resolves to a role with no grants, so
  // the gateway would refuse anyway — but it would refuse with 403, and "you are not signed in" is
  // the more useful answer and the one the client turns into a login prompt rather than an error.
  router.use(requireSignIn());

  /**
   * A page of records, narrowed and ordered by the request.
   *
   * `filter` may be repeated: `?filter=status:eq:open&filter=amount:gt:100`. `sort` is a
   * comma-separated list where a leading minus means descending: `?sort=-spent_on,description`.
   */
  router.get(
    "/",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      const page = await gateway.list(
        parseFilters(queryList(request, "filter")),
        parseSort(queryOne(request, "sort")),
        integer(queryOne(request, "skip"), 0),
        integer(queryOne(request, "take"), 50),
      );
      response.json(page);
    }),
  );

  /**
   * One figure, or one series of them: `?op=sum&field=amount&groupBy=category`.
   *
   * Before `/:id`, or `aggregate` is read as an id and the answer is a 404 for a record nobody
   * asked for.
   *
   * `groupBy` also accepts `month_of:<field>`, because "spend per month" is the question a chart
   * asks and "spend per day, added up by the reader" is not.
   */
  router.get(
    "/aggregate",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      const result = await gateway.aggregateRows(
        queryOne(request, "op") ?? "count",
        queryOne(request, "field") ?? null,
        queryOne(request, "groupBy") ?? null,
        parseFilters(queryList(request, "filter")),
      );
      response.json(result);
    }),
  );

  router.get(
    "/:id",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      response.json(await gateway.get(String(request.params["id"])));
    }),
  );

  router.post(
    "/",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      const created = await gateway.create(request.body);
      response.status(201).location(`${request.baseUrl}/${created["id"] as string}`).json(created);
    }),
  );

  /** Replace a record. Every field the entity has is written, so a field absent from the body is
   * cleared — which is what replace means, and why PATCH exists beside it. */
  router.put(
    "/:id",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      response.json(
        await gateway.write(String(request.params["id"]), request.body, gateway.fieldKeys),
      );
    }),
  );

  /** Change the fields the body names and leave the rest alone. */
  router.patch(
    "/:id",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      response.json(
        await gateway.write(
          String(request.params["id"]),
          request.body,
          gateway.suppliedKeys(request.body),
        ),
      );
    }),
  );

  router.delete(
    "/:id",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      await gateway.delete(String(request.params["id"]));
      response.status(204).end();
    }),
  );

  /**
   * Run a command against one record.
   *
   * A command is not an update with a different name: it is a business action the definition
   * declared, with its own permission, its own legality rule about which states it may run from,
   * and its own required input. All three are checked before anything is written.
   */
  router.post(
    "/:id/commands/:command",
    handler(async (request: Request, response: Response) => {
      const gateway = state(request).scope.gateway<RecordRow>(entity);
      const result = await gateway.runCommand(
        String(request.params["id"]),
        String(request.params["command"]),
        request.body,
      );
      response.json({ record: result.record, message: result.message });
    }),
  );

  return router;
}

/** A repeated query parameter, as a list. Express hands back a string for one and an array for
 * several, and a caller that forgot the difference would silently filter on nothing. */
function queryList(request: Request, name: string): string[] {
  const value = request.query[name];
  if (value === undefined) return [];
  if (Array.isArray(value)) return value.map((entry) => String(entry));
  return [String(value)];
}

/** A single-valued query parameter. The LAST wins when it was sent more than once, which is what
 * a browser's own form handling does. */
function queryOne(request: Request, name: string): string | null {
  const value = request.query[name];
  if (value === undefined) return null;
  if (Array.isArray(value)) {
    const last = value[value.length - 1];
    return last === undefined ? null : String(last);
  }
  return String(value);
}

function integer(value: string | null, fallback: number): number {
  if (value === null) return fallback;
  const parsed = Number(value);
  return Number.isInteger(parsed) ? parsed : fallback;
}
