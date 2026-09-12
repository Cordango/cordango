// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

/**
 * Turns an error code into a sentence in the reader's language.
 *
 * **The code travels, the sentence does not.** Everything below the boundary — stores, hooks,
 * permission checks — raises a `RecordError` carrying a dotted code and an English sentence as a
 * fallback. Only here, at the edge, does anything decide which language to answer in. That is what
 * keeps a translation from having to be threaded through every layer, and what keeps a client
 * switching on `record.not_found` from breaking when somebody improves the wording.
 */
export interface ApiMessages {
  /** The message for this code, or `fallback` when the code has no entry — a missing translation
   * should degrade to an English sentence, never to a blank or to the code itself. */
  translate(code: string, fallback: string, acceptLanguage?: string | undefined): string;
}

/** The default when an application has not set up translations: the fallback, as written. */
export const passThroughMessages: ApiMessages = {
  translate: (_code, fallback) => fallback,
};

/**
 * Languages to try, best first, ending with the application's default.
 *
 * Ordered by the q-value the client sent, which is the client saying which of several acceptable
 * languages it actually prefers. Ignoring it and taking the first listed works for the common
 * one-language case and quietly picks wrong for everyone else.
 */
export function preferredLanguages(header: string | undefined, fallback: string): string[] {
  const ranked: { language: string; quality: number }[] = [];

  for (const entry of (header ?? "").split(",")) {
    const trimmed = entry.trim();
    if (trimmed === "") continue;

    const parts = trimmed.split(";").map((part) => part.trim());
    // Only the primary subtag is read, so `de-AT` and `de-CH` both find `de`. An application that
    // genuinely differs by region adds the fuller tag as its own file and it is found first.
    const language = (parts[0] ?? "").split("-")[0]?.toLowerCase() ?? "";
    if (language === "") continue;

    let quality = 1;
    for (const parameter of parts.slice(1)) {
      if (!parameter.toLowerCase().startsWith("q=")) continue;
      const parsed = Number(parameter.slice(2));
      if (Number.isFinite(parsed)) quality = parsed;
    }

    if (quality > 0) ranked.push({ language, quality });
  }

  ranked.sort((a, b) => b.quality - a.quality);
  return [...ranked.map((entry) => entry.language), fallback];
}

/**
 * Messages from a JSON file per language, picked by the request's `Accept-Language`.
 *
 * Read once at startup. Messages change when the application is redeployed, and re-reading a file
 * on every 404 would be a strange place to spend a syscall.
 */
export class JsonApiMessages implements ApiMessages {
  private readonly byLanguage = new Map<string, Record<string, string>>();

  constructor(
    resourceDirectory: string,
    private readonly defaultLanguage = "en",
  ) {
    let entries: string[];
    try {
      entries = readdirSync(resourceDirectory);
    } catch {
      // No resource directory is the ordinary state of an application that has not set up
      // translations. Every code then falls through to its English fallback.
      return;
    }

    for (const entry of entries) {
      const match = /^messages\.(.+)\.json$/.exec(entry);
      if (match === null) continue;

      const language = (match[1] ?? "").toLowerCase();
      try {
        const table: unknown = JSON.parse(readFileSync(join(resourceDirectory, entry), "utf8"));
        if (typeof table === "object" && table !== null)
          this.byLanguage.set(language, table as Record<string, string>);
      } catch {
        // A malformed message file must not stop the application booting. The codes it would have
        // translated answer in English instead, which is the same degradation as a missing entry.
      }
    }
  }

  translate(code: string, fallback: string, acceptLanguage?: string | undefined): string {
    for (const language of preferredLanguages(acceptLanguage, this.defaultLanguage)) {
      const message = this.byLanguage.get(language)?.[code];
      if (message !== undefined) return message;
    }
    return fallback;
  }
}
