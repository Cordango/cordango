// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { Request, Response } from "express";

/**
 * Cookies, read and written without a middleware.
 *
 * Two cookies carry this application's whole browser-facing security model — the session and the
 * antiforgery token — and both are set here. A package would be three lines of value for a
 * dependency in the path of every request, and the attributes below are the part that matters:
 * `HttpOnly` on one and deliberately NOT on the other is the entire double-submit mechanism.
 */
export interface CookieOptions {
  /** Kept from script. The session cookie sets it; the antiforgery cookie must not, because the
   * page has to be able to read that one and echo it back. */
  httpOnly?: boolean;
  /** Over HTTPS only. On by default in production and off on a plain-HTTP dev origin, or the
   * cookie is set and never sent back. */
  secure?: boolean;
  sameSite?: "Strict" | "Lax" | "None";
  /** Seconds. Absent means a session cookie, which the browser drops when it closes. */
  maxAge?: number;
  path?: string;
}

/** Every cookie on the request, by name. Malformed pairs are skipped rather than throwing: a
 * browser can carry a cookie this application did not set and did not shape. */
export function readCookies(header: string | undefined): Record<string, string> {
  const cookies: Record<string, string> = {};
  if (header === undefined) return cookies;

  for (const part of header.split(";")) {
    const separator = part.indexOf("=");
    if (separator < 0) continue;

    const name = part.slice(0, separator).trim();
    if (name === "") continue;

    try {
      cookies[name] = decodeURIComponent(part.slice(separator + 1).trim());
    } catch {
      cookies[name] = part.slice(separator + 1).trim();
    }
  }

  return cookies;
}

/** One cookie from the request. */
export function cookie(request: Request, name: string): string | undefined {
  return readCookies(request.headers.cookie)[name];
}

/** `Set-Cookie`, appended rather than assigned — a login sets the session and the antiforgery
 * token in one response, and assigning would leave the browser with only the second. */
export function setCookie(
  response: Response,
  name: string,
  value: string,
  options: CookieOptions = {},
): void {
  const parts = [`${name}=${encodeURIComponent(value)}`];
  parts.push(`Path=${options.path ?? "/"}`);
  if (options.maxAge !== undefined) parts.push(`Max-Age=${Math.floor(options.maxAge)}`);
  if (options.httpOnly === true) parts.push("HttpOnly");
  if (options.secure === true) parts.push("Secure");
  parts.push(`SameSite=${options.sameSite ?? "Lax"}`);

  append(response, parts.join("; "));
}

/** Expire a cookie. The attributes have to match the ones it was set with or the browser keeps
 * the original and the sign-out does nothing. */
export function clearCookie(response: Response, name: string, options: CookieOptions = {}): void {
  setCookie(response, name, "", { ...options, maxAge: 0 });
}

function append(response: Response, value: string): void {
  const existing = response.getHeader("Set-Cookie");
  if (existing === undefined) response.setHeader("Set-Cookie", [value]);
  else if (Array.isArray(existing)) response.setHeader("Set-Cookie", [...existing, value]);
  else response.setHeader("Set-Cookie", [String(existing), value]);
}
