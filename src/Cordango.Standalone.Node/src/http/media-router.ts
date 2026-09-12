// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { Router, type Request, type Response } from "express";
import busboy from "busboy";
import { pipeline } from "node:stream/promises";
import type { Readable } from "node:stream";
import { RecordError } from "../records/errors.js";
import { readStored, type FileStore, type StoredFile } from "../media/file-store.js";
import { state } from "./context.js";
import { handler } from "./error-handler.js";
import { requireSignIn } from "./guards.js";

/**
 * Uploading and fetching the files that attachment fields point at.
 *
 * Signing in is the whole of the read rule, and that is a deliberate simplification worth stating
 * plainly: a reference is a 64-character content hash, so it cannot be guessed, but anyone signed
 * in who HAS a reference can fetch the file behind it — including one attached to a record their
 * role cannot read. Tying a file to the record that points at it would fix that, and it needs a
 * reverse index this target does not build yet. Until then the limit is documented rather than
 * implied.
 */
export function mediaRouter(files: FileStore): Router {
  const router = Router();
  router.use(requireSignIn());

  router.post(
    "/",
    handler(async (request: Request, response: Response) => {
      const stored = await readUpload(request, files);
      response.json(stored);
    }),
  );

  router.get(
    "/:reference",
    handler(async (request: Request, response: Response) => {
      const found = await files.open(String(request.params["reference"]));
      if (found === null) throw new RecordError("media.not_found", "No file with that reference.", 404);

      // The uploader chose this content type, so the browser must not be allowed to second-guess it
      // into something executable. Without the nosniff header a file uploaded as text/plain can
      // still be sniffed as HTML and run as script on this origin.
      response.setHeader("X-Content-Type-Options", "nosniff");
      response.setHeader("Content-Type", found.meta.contentType || "application/octet-stream");
      response.setHeader(
        "Content-Disposition",
        `inline; filename="${found.meta.fileName}"; filename*=UTF-8''${encodeURIComponent(found.meta.fileName)}`,
      );

      await pipeline(readStored(found.path), response);
    }),
  );

  router.delete(
    "/:reference",
    handler(async (request: Request, response: Response) => {
      // Content addressing means one file can be the target of many records' fields. Deleting it is
      // therefore an administrative act with consequences the caller cannot see from here.
      if (!state(request).user.isAdministrator)
        throw RecordError.forbidden("media.delete_denied", "Only an administrator may delete files.");

      const removed = await files.delete(String(request.params["reference"]));
      if (!removed) throw new RecordError("media.not_found", "No file with that reference.", 404);

      response.status(204).end();
    }),
  );

  return router;
}

/**
 * The one file out of a multipart body.
 *
 * Streamed into the store rather than buffered: an upload the size of the machine's memory must not
 * be the way to take the application down, and the store is refusing on a byte count as it goes.
 * Only the FIRST file part is read — the client sends one, and accepting several here would be a
 * surface nothing asked for.
 */
function readUpload(request: Request, files: FileStore): Promise<StoredFile> {
  return new Promise<StoredFile>((resolve, reject) => {
    let contentType: string;
    try {
      contentType = String(request.headers["content-type"] ?? "");
      if (!contentType.toLowerCase().startsWith("multipart/form-data"))
        throw new RecordError("media.not_multipart", "Send the file as multipart/form-data.");
    } catch (error) {
      reject(error);
      return;
    }

    const parser = busboy({ headers: request.headers, limits: { files: 1 } });
    let handled = false;

    parser.on("file", (_name: string, stream: Readable, info: { filename: string; mimeType: string }) => {
      handled = true;
      files
        .save(stream, info.filename, info.mimeType || "application/octet-stream")
        .then(resolve)
        .catch((error: unknown) => {
          // The stream has to be drained even when the save gave up, or the request socket stays
          // open until it times out and the client sees a hang rather than the refusal.
          stream.resume();
          reject(error);
        });
    });

    parser.on("close", () => {
      if (!handled) reject(new RecordError("media.empty", "No file was sent."));
    });

    parser.on("error", (error: unknown) => reject(error));

    request.pipe(parser);
  });
}
