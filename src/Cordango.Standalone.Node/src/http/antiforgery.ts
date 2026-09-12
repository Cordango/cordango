// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";
import type { NextFunction, Request, RequestHandler, Response } from "express";
import { RecordError } from "../records/errors.js";
import { clearCookie, cookie, setCookie } from "./cookies.js";

/**
 * Every state-changing request carries an antiforgery token, on every route, without anybody
 * opting in.
 *
 * **The mechanism, in one paragraph.** The server issues a token as a cookie the page CAN read —
 * `XSRF-TOKEN`, deliberately not `HttpOnly` — and requires the same value back in the
 * `X-XSRF-TOKEN` header on anything unsafe. A form on another origin can make the browser send
 * the cookie, because that is what browsers do; it cannot READ the cookie to set the header,
 * because the same-origin policy stops it. That asymmetry is the whole defence.
 *
 * **Bound to the session, not just echoed.** A bare double-submit accepts any pair where the two
 * halves match, so an attacker who can set a cookie on the origin — a subdomain, a sloppy
 * intermediary — can forge both halves. The token here is `nonce.HMAC(nonce, sessionId)`, so a
 * token minted for one session is refused on another, and a token minted for nobody is refused
 * once somebody signs in.
 */
export const antiforgeryCookie = "XSRF-TOKEN";
export const antiforgeryHeader = "x-xsrf-token";

/** Requests that must not change anything, and so need no token. Requiring one on a GET would
 * only break links. */
const safeMethods = new Set(["GET", "HEAD", "OPTIONS", "TRACE"]);

export interface AntiforgeryOptions {
  /** The key the binding HMAC is taken under. Rotating it invalidates every outstanding token,
   * which is a sign-out for every open tab and nothing worse. */
  readonly secret: string;
  /** Set `Secure` on the cookie. Off on a plain-HTTP dev origin, where a secure cookie is set and
   * never sent back. */
  readonly secure: boolean;
}

/** A fresh token bound to this session id — or to the anonymous caller, so the login form itself
 * has one to send. */
export function issueToken(options: AntiforgeryOptions, sessionId: string | null): string {
  const nonce = randomBytes(24).toString("base64url");
  return `${nonce}.${bind(options.secret, nonce, sessionId)}`;
}

/** True when `token` was minted by this application for this session. */
export function verifyToken(
  options: AntiforgeryOptions,
  token: string | undefined,
  sessionId: string | null,
): boolean {
  if (token === undefined) return false;

  const separator = token.lastIndexOf(".");
  if (separator <= 0) return false;

  const nonce = token.slice(0, separator);
  const signature = token.slice(separator + 1);
  const expected = bind(options.secret, nonce, sessionId);

  // Constant time, because a comparison that returns early leaks how much of the signature was
  // right — one byte per attempt is enough to forge one given enough attempts.
  const left = Buffer.from(signature);
  const right = Buffer.from(expected);
  return left.length === right.length && timingSafeEqual(left, right);
}

function bind(secret: string, nonce: string, sessionId: string | null): string {
  return createHmac("sha256", secret).update(`${nonce}:${sessionId ?? ""}`).digest("base64url");
}

/**
 * Puts a token on the response when the browser does not have a valid one, and refuses any unsafe
 * request that does not echo it.
 *
 * `sessionId` is read per request rather than captured, because the binding changes the moment
 * somebody signs in or out and a stale token would then be refused on the next write.
 */
export function antiforgery(
  options: AntiforgeryOptions,
  sessionId: (request: Request) => string | null,
  exempt: (request: Request) => boolean = () => false,
): RequestHandler {
  return (request: Request, response: Response, next: NextFunction): void => {
    const session = sessionId(request);
    const present = cookie(request, antiforgeryCookie);

    // Re-issued whenever the cookie is missing or no longer binds — which is exactly the moment a
    // session begins or ends, so a page that reloads after signing in reads a token that works.
    if (!verifyToken(options, present, session))
      setCookie(response, antiforgeryCookie, issueToken(options, session), {
        httpOnly: false,
        secure: options.secure,
        sameSite: "Lax",
      });

    if (safeMethods.has(request.method) || exempt(request)) {
      next();
      return;
    }

    const header = request.headers[antiforgeryHeader];
    const sent = Array.isArray(header) ? header[0] : header;

    // Both halves, and both bound to this session. A stale token is a routine thing that happens
    // to a tab left open overnight, and the client can fix it by reloading — which is why this is
    // its own code rather than a server error that sends the reader looking in the wrong place.
    if (!verifyToken(options, present, session) || !verifyToken(options, sent, session) || sent !== present) {
      next(
        new RecordError(
          "request.csrf_invalid",
          "Your session token was missing or out of date. Reload and try again.",
          400,
        ),
      );
      return;
    }

    next();
  };
}

/** Drop the token, so the next response mints one bound to whoever is signed in now. Called on
 * sign-in and sign-out, where the binding has just changed. */
export function rotate(response: Response, options: AntiforgeryOptions, sessionId: string | null): void {
  clearCookie(response, antiforgeryCookie, { httpOnly: false, secure: options.secure, sameSite: "Lax" });
  setCookie(response, antiforgeryCookie, issueToken(options, sessionId), {
    httpOnly: false,
    secure: options.secure,
    sameSite: "Lax",
  });
}
