// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { createHash, randomUUID } from "node:crypto";
import { createReadStream, createWriteStream } from "node:fs";
import { mkdir, rename, rm, stat, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { pipeline } from "node:stream/promises";
import type { Readable } from "node:stream";
import { RecordError } from "../records/errors.js";

/** What an attachment field's value points at. */
export interface StoredFile {
  /** The opaque token stored in the record's field. */
  reference: string;
  /** What the file was called when it was uploaded. */
  fileName: string;
  /** As declared by the uploader, and never trusted for anything but the download header. */
  contentType: string;
  length: number;
}

/** Where uploaded files live. One implementation ships; an application that wants object storage
 * writes another and hands it to the media router. */
export interface FileStore {
  save(content: Readable, fileName: string, contentType: string): Promise<StoredFile>;
  open(reference: string): Promise<{ meta: StoredFile; path: string } | null>;
  delete(reference: string): Promise<boolean>;
}

/**
 * Files on the local disk, addressed by the hash of their contents.
 *
 * **Content addressing, for two reasons.** The same document attached to forty records is stored
 * once. And the reference cannot be guessed or walked: it is a hash, so there is no `../` to
 * smuggle through it and no sequence to enumerate. The original file name is metadata rather than a
 * path, which is what keeps a file called `..\..\web.config` from being a problem.
 */
export class LocalFileStore implements FileStore {
  private readonly root: string;
  private ready: Promise<void> | null = null;

  constructor(
    root: string,
    /** Bytes above which an upload is refused, mid-stream, before the whole of it is on disk. */
    private readonly maxBytes = 25 * 1024 * 1024,
  ) {
    this.root = resolve(root);
  }

  async save(content: Readable, fileName: string, contentType: string): Promise<StoredFile> {
    await this.ensureRoot();

    // Staged to a temporary file first: the reference is the hash of the content, and the hash is
    // not known until the last byte has been read.
    const staging = join(this.root, `incoming-${randomUUID().replaceAll("-", "")}`);
    const hash = createHash("sha256");
    const max = this.maxBytes;
    let length = 0;

    try {
      await pipeline(
        content,
        async function* (source: AsyncIterable<Buffer>) {
          for await (const chunk of source) {
            length += chunk.length;
            // Refused mid-stream rather than after. Reading the whole of a two-gigabyte upload to
            // then say it was too big is a denial of service the limit was supposed to prevent.
            if (length > max) throw tooLarge(max);
            hash.update(chunk);
            yield chunk;
          }
        },
        createWriteStream(staging),
      );

      if (length === 0) throw new RecordError("media.empty", "No file was sent.");

      const reference = hash.digest("hex");
      const target = this.pathFor(reference);

      // Already there: the same bytes under the same hash. Keep the first copy and drop the second,
      // which is the whole point of addressing by content.
      let exists = true;
      try {
        await stat(target);
      } catch {
        exists = false;
      }

      if (exists) await rm(staging, { force: true });
      else await rename(staging, target);

      const meta: StoredFile = { reference, fileName: safeName(fileName), contentType, length };
      await writeFile(`${target}.json`, JSON.stringify(meta), "utf8");
      return meta;
    } catch (error) {
      await rm(staging, { force: true });
      throw error;
    }
  }

  async open(reference: string): Promise<{ meta: StoredFile; path: string } | null> {
    if (!isReference(reference)) return null;

    const path = this.pathFor(reference);
    try {
      const meta: unknown = JSON.parse(await readFile(`${path}.json`, "utf8"));
      await stat(path);
      if (typeof meta !== "object" || meta === null) return null;
      return { meta: meta as StoredFile, path };
    } catch {
      return null;
    }
  }

  async delete(reference: string): Promise<boolean> {
    if (!isReference(reference)) return false;

    const path = this.pathFor(reference);
    try {
      await stat(path);
    } catch {
      return false;
    }

    await rm(path, { force: true });
    await rm(`${path}.json`, { force: true });
    return true;
  }

  /** The path a reference maps to. Built from a validated hash, so it cannot leave the root however
   * the reference was spelled. */
  private pathFor(reference: string): string {
    return join(this.root, reference);
  }

  private ensureRoot(): Promise<void> {
    this.ready ??= mkdir(this.root, { recursive: true }).then(() => undefined);
    return this.ready;
  }
}

/** A reference is 64 hex characters and nothing else. Checked before the value ever reaches a path,
 * which is what makes traversal unexpressible rather than merely difficult. */
function isReference(value: string): boolean {
  return /^[0-9a-f]{64}$/.test(value);
}

/** The uploaded name, stripped of anything that makes it a path. Metadata for the download header;
 * never used to locate the file. */
function safeName(fileName: string): string {
  const base = fileName.split(/[\\/]/).pop() ?? "";

  // Control characters and the double quote, which are what break a Content-Disposition header.
  // Everything else - spaces, unicode, punctuation - is kept: the name is metadata for the person
  // downloading the file and is never a path. Written as a filter rather than a character class so
  // that no control character has to appear in this source file to describe one.
  const cleaned = [...base]
    .filter((character) => {
      const code = character.codePointAt(0) ?? 0;
      return code > 31 && code !== 127 && character !== '"';
    })
    .join("")
    .trim();

  return cleaned === "" || cleaned === "." || cleaned === ".." ? "file" : cleaned;
}

function tooLarge(max: number): RecordError {
  return new RecordError(
    "media.too_large",
    `That file is larger than the ${Math.floor(max / (1024 * 1024))} MB limit.`,
    413,
  );
}

/** A read stream for a stored file. */
export function readStored(path: string): Readable {
  return createReadStream(path);
}
